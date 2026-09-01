using Nikami.Aurora.GodotRuntime.Application.Abstractions;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;

public sealed class DaoScriptBytecodeProvider(IRuntimeEnvironment environment)
{
    public byte[]? Load(string scriptResRef, out string error)
    {
        var root = environment.Get("OPENDAO_SCRIPT_ROOT")?.Trim() ?? string.Empty;
        var normalized = Path.GetFileNameWithoutExtension(scriptResRef.Trim()).ToLowerInvariant();
        if (root.Length == 0)
        {
            error = "script-root-absent";
            return null;
        }
        if (normalized.Length == 0 || normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            error = "script-resref-invalid";
            return null;
        }
        var path = Path.Combine(root, normalized + ".ncs");
        if (!File.Exists(path))
        {
            error = "script-bytecode-absent";
            return null;
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            error = string.Empty;
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = "script-bytecode-read-failed";
            return null;
        }
    }
}
