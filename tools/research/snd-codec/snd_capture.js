// Frida script: capture THUG2 PC's DECODED sound buffers.
//
// The .snd codec is undecoded (see snd_codec_fit.py). The engine decodes each
// .snd into a DirectSound buffer, so hooking the buffer write gives us the
// decoder's exact output for a file whose input we control -- which is what
// turns recovering the predictor from a search into arithmetic.
//
// It hooks two things:
//   CreateFileA/W   so each capture is labelled with the .snd the game just
//                   opened. Without this you get PCM with no idea which sound
//                   it came from.
//   IDirectSoundBuffer::Lock / Unlock
//                   the COM vtable slots (Lock = 11, Unlock = 19 in the
//                   IDirectSoundBuffer vtable). Lock hands the game a writable
//                   pointer plus a length; Unlock means it has finished
//                   writing. We snapshot the region at Unlock.
//
// Hooking is by injection, not by debugger attach, so SafeDisc's
// IsDebuggerPresent checks do not trip.
//
// Usage (in the VM, from an Admin prompt):
//     pip install frida-tools
//     mkdir snd_capture
//     frida -f "C:\\path\\to\\THUG2.exe" -l snd_capture.js -o capture.log
//   or attach to the running game:
//     frida -n THUG2.exe -l snd_capture.js
//
// Buffers land in CAPTURE_DIR as raw signed 16-bit LE mono, named
// <sound>_<n>.raw. Feed them to snd_solve.py.

'use strict';

var CAPTURE_DIR = '.\\snd_capture\\';
var MIN_BYTES = 512;      // ignore tiny control writes
var MAX_FILES = 200;      // safety stop

var lastSound = '(unknown)';
var written = 0;
var pending = {};         // lock cookie -> { ptr, bytes }

function log(message) {
    console.log('[snd] ' + message);
}

function sanitise(name) {
    return name.replace(/[^A-Za-z0-9_.-]/g, '_');
}

// --- which .snd is being loaded ------------------------------------------

['CreateFileA', 'CreateFileW'].forEach(function (name) {
    var addr = Module.findExportByName('kernel32.dll', name);
    if (!addr) return;
    var wide = name.endsWith('W');
    Interceptor.attach(addr, {
        onEnter: function (args) {
            try {
                var path = wide ? args[0].readUtf16String() : args[0].readAnsiString();
                if (path && /\.snd$/i.test(path)) {
                    lastSound = sanitise(path.split('\\').pop());
                    log('opened ' + lastSound);
                }
            } catch (e) { /* not a readable path */ }
        }
    });
});

// --- the decoded PCM -----------------------------------------------------

function dump(ptr, bytes) {
    if (bytes < MIN_BYTES || written >= MAX_FILES) return;
    try {
        var data = ptr.readByteArray(bytes);
        var path = CAPTURE_DIR + lastSound + '_' + written + '.raw';
        var file = new File(path, 'wb');
        file.write(data);
        file.flush();
        file.close();
        written++;
        log('wrote ' + path + ' (' + bytes + ' bytes = ' + (bytes / 2) + ' samples)');
    } catch (e) {
        log('dump failed: ' + e);
    }
}

// IDirectSoundBuffer vtable: 0 QueryInterface, 1 AddRef, 2 Release, ...,
// 11 Lock, ..., 19 Unlock. Resolved from a live interface pointer the first
// time the game creates a buffer, so we do not hardcode a module offset.
function hookBufferVtable(bufferPtr) {
    var vtable = bufferPtr.readPointer();
    var lockAddr = vtable.add(11 * Process.pointerSize).readPointer();
    var unlockAddr = vtable.add(19 * Process.pointerSize).readPointer();

    Interceptor.attach(lockAddr, {
        onEnter: function (args) {
            // Lock(this, offset, bytes, ppAudio1, pBytes1, ppAudio2, pBytes2, flags)
            this.ppAudio1 = args[3];
            this.pBytes1 = args[4];
        },
        onLeave: function () {
            try {
                var ptr = this.ppAudio1.readPointer();
                var bytes = this.pBytes1.readU32();
                pending[ptr.toString()] = { ptr: ptr, bytes: bytes };
            } catch (e) { /* failed lock */ }
        }
    });

    Interceptor.attach(unlockAddr, {
        onEnter: function (args) {
            // Unlock(this, pAudio1, bytes1, pAudio2, bytes2) -- the game has
            // finished writing decoded PCM into the locked region.
            try {
                var ptr = args[1];
                var bytes = args[2].toInt32();
                var record = pending[ptr.toString()];
                dump(ptr, bytes > 0 ? bytes : (record ? record.bytes : 0));
                delete pending[ptr.toString()];
            } catch (e) { /* not a buffer we tracked */ }
        }
    });

    log('hooked IDirectSoundBuffer Lock/Unlock');
}

// CreateSoundBuffer(this, pcDSBufferDesc, ppDSBuffer, pUnkOuter) is the first
// place a buffer interface pointer exists. Hook the IDirectSound vtable slot 3.
var hooked = false;
var dsCreate = Module.findExportByName('dsound.dll', 'DirectSoundCreate8')
    || Module.findExportByName('dsound.dll', 'DirectSoundCreate');

if (dsCreate) {
    Interceptor.attach(dsCreate, {
        onEnter: function (args) { this.ppDS = args[1]; },
        onLeave: function () {
            if (hooked) return;
            try {
                var ds = this.ppDS.readPointer();
                var vtable = ds.readPointer();
                var createBuffer = vtable.add(3 * Process.pointerSize).readPointer();
                Interceptor.attach(createBuffer, {
                    onEnter: function (args) { this.ppBuffer = args[2]; },
                    onLeave: function () {
                        if (hooked) return;
                        try {
                            hookBufferVtable(this.ppBuffer.readPointer());
                            hooked = true;
                        } catch (e) { /* try the next buffer */ }
                    }
                });
                log('hooked IDirectSound::CreateSoundBuffer');
            } catch (e) {
                log('could not reach the IDirectSound vtable: ' + e);
            }
        }
    });
    log('waiting for DirectSound init...');
} else {
    log('dsound.dll not loaded yet - attach after the game reaches its menu');
}
