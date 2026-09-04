using Argus.Orchestrator.Config;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Parity pin between InputValidator.cs and orchestrator/ui/src/validation/detectorParams.ts.
///
/// The rmad message strings live in TWO files by necessity — C# validates the POST body, TS
/// validates the form — and a drift between them is invisible until an operator hits a Save
/// the browser had already called valid, with a message the UI cannot attach to any field.
/// These read the client source off disk and assert the server's constants appear in it
/// verbatim, so editing one side alone turns red here instead of in production.
///
/// The check lives on the C# side deliberately: the SPA has no @types/node, so a vitest that
/// read this file back would break `tsc -b` in the SPA build.
/// </summary>
public class ClientMessageParityTests
{
    private const string ClientValidator = "orchestrator/ui/src/validation/detectorParams.ts";
    private const string ServerValidator = "orchestrator/Argus.Orchestrator/Config/InputValidator.cs";

    public static TheoryData<string> ParityMessages() => new()
    {
        InputValidator.MSG_WINDOW_RANGE,
        InputValidator.MSG_MIN_SAMPLES,
        InputValidator.MSG_MIN_SAMPLES_LE_WINDOW,
        InputValidator.MSG_RMAD_LEGACY_N_TREES,
    };

    [Theory]
    [MemberData(nameof(ParityMessages))]
    public void ServerMessage_HasVerbatimClientMirror(string message)
    {
        var client = File.ReadAllText(FindRepoFile(ClientValidator));

        Assert.True(
            client.Contains(message, StringComparison.Ordinal),
            $"detectorParams.ts no longer carries the server message \"{message}\" verbatim — " +
            "the form will report a value as valid that the server then rejects.");
    }

    // A UTF-8 BOM in front of `using` is invisible in an editor and harmless to the compiler,
    // but it was added by hand on this branch and makes the file the odd one out next to the
    // sources it sits with. Keep it out.
    [Fact]
    public void ServerValidatorSource_CarriesNoByteOrderMark()
    {
        var bytes = File.ReadAllBytes(FindRepoFile(ServerValidator));

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(hasBom, $"{ServerValidator} starts with a UTF-8 BOM.");
    }

    /// <summary>Resolves a repo-relative path by walking up from the test binary.</summary>
    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"could not find {relativePath} above {AppContext.BaseDirectory}");
    }
}
