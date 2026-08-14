using System.Diagnostics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class BlendModelExporter : IModelExporter
{
    private readonly Action<string, string, ModelDocument, string, CancellationToken> _runHelper;

    public BlendModelExporter()
        : this(RunHelper)
    {
    }

    internal BlendModelExporter(
        Action<string, string, ModelDocument, string, CancellationToken> runHelper)
    {
        _runHelper = runHelper ?? throw new ArgumentNullException(nameof(runHelper));
    }

    public MeshExportResult Export(ModelDocument document, MeshExportRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.OutputDirectory);

        var helperPath = BlenderLocator.Resolve(request.BlenderHelperPath, out var failureReason)
                         ?? throw new InvalidOperationException(failureReason);
        var scriptPath = ResolveScriptPath()
                         ?? throw new InvalidOperationException(
                             "Blender export script was not found. Expected BlenderExporter/import_package.py next to the app.");

        var stem = request.OutputStem ?? document.Name;
        var outputPath = Path.Combine(request.OutputDirectory, stem + ".blend");
        var stagedOutputPath = Path.Combine(
            request.OutputDirectory,
            "." + Guid.NewGuid().ToString("N") + ".tmp.blend");

        try
        {
            _runHelper(helperPath, scriptPath, document, stagedOutputPath, request.CancellationToken);
            if (!IsNonEmptyRegularFile(stagedOutputPath))
            {
                throw new InvalidOperationException(
                    "Blender export helper completed successfully but did not produce a non-empty .blend file.");
            }

            File.Move(stagedOutputPath, outputPath, true);
        }
        finally
        {
            TryDeleteStagedOutput(stagedOutputPath);
        }

        return new MeshExportResult
        {
            OutputPaths = [outputPath],
            Triangles = document.TriangleCount > 0
                ? document.TriangleCount
                : document.Meshes.SelectMany(static mesh => mesh.Primitives)
                    .Sum(static primitive => primitive.TriangleCount),
            MaterialCount = document.Materials.Count,
            TextureCount = document.Textures.Count
        };
    }

    private static bool IsNonEmptyRegularFile(string path)
    {
        if (!File.Exists(path)) return false;

        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
               && new FileInfo(path).Length > 0;
    }

    private static string? ResolveScriptPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        return File.Exists(path) ? path : null;
    }

    private static void RunHelper(
        string helperPath,
        string scriptPath,
        ModelDocument document,
        string blendPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add("--background");
        process.StartInfo.ArgumentList.Add("--factory-startup");
        process.StartInfo.ArgumentList.Add("--python-exit-code");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("--python");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add("--stdin-zip");

        if (!process.Start())
            throw new InvalidOperationException("Failed to start Blender export helper.");

        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            BlendPackageWriter.Write(document, process.StandardInput.BaseStream, blendPath);
            process.StandardInput.Close();
        }
        catch
        {
            TryKill(process);
            throw;
        }

        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Blender export helper failed with exit code " + process.ExitCode + "." +
                Environment.NewLine + output + Environment.NewLine + error);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // Cancellation should not be masked by process cleanup failures.
        }
    }

    private static void TryDeleteStagedOutput(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(path);
            }
            else if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staged-output cleanup must not mask the export result.
        }
    }
}
