using System.Diagnostics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class BlendModelExporter : IModelExporter
{
    public MeshExportResult Export(ModelDocument document, MeshExportRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.OutputDirectory);

        var helperPath = ResolveHelperPath(request.BlenderHelperPath)
                         ?? throw new InvalidOperationException(
                             "Blender export helper was not found. Expected a bundled helper at " +
                             Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "blender.exe") +
                             " or pass an explicit helper path.");
        var scriptPath = ResolveScriptPath()
                         ?? throw new InvalidOperationException(
                             "Blender export script was not found. Expected BlenderExporter/import_package.py next to the app.");

        var stem = request.OutputStem ?? document.Name;
        var outputPath = Path.Combine(request.OutputDirectory, stem + ".blend");

        RunHelper(helperPath, scriptPath, document, outputPath, request.CancellationToken);

        return new MeshExportResult
        {
            OutputPaths = File.Exists(outputPath) ? [outputPath] : [],
            Triangles = document.TriangleCount > 0
                ? document.TriangleCount
                : document.Meshes.SelectMany(static mesh => mesh.Primitives)
                    .Sum(static primitive => primitive.TriangleCount),
            MaterialCount = document.Materials.Count,
            TextureCount = document.Textures.Count
        };
    }

    private static string? ResolveHelperPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "BlenderExporter", "blender.exe"),
            Path.Combine(baseDir, "BlenderExporter", "blender", "blender.exe"),
            Path.Combine(baseDir, "BlenderExporter", "blender")
        };

        return candidates.FirstOrDefault(File.Exists);
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
}
