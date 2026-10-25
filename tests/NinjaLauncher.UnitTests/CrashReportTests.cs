using NinjaLauncher.Minecraft.Java.Diagnostics;

namespace NinjaLauncher.UnitTests;

public sealed class CrashReportTests
{
    private const string Vanilla = """
        ---- Minecraft Crash Report ----
        // Who set us up the TNT?

        Time: 2026-10-25 22:10:11
        Description: Rendering overlay

        java.lang.NullPointerException: Cannot invoke "Object.toString()" because "x" is null
        	at net.minecraft.client.Foo.bar(Foo.java:10)
        	at net.minecraft.client.Main.main(Main.java:1)
        Caused by: java.lang.IllegalStateException: inner
        	at a.b(c.java:1)
        Caused by: java.lang.OutOfMemoryError: Java heap space
        	at a.b(c.java:2)

        A detailed walkthrough of the error, its code path and all known details is as follows:
        """;

    private const string NeoForge = """
        ---- Minecraft Crash Report ----
        Description: Mod loading failures have occurred; consult the issue messages for more details

        net.neoforged.fml.ModLoadingException: Mod loading has failed
        	at x.y(z.java:1)

        -- Head --
        Thread: main
        Suspected Mods:
        	Sodium (sodium), Version: 0.6.0
        	Iris (iris), Version: 1.8.0
        Stacktrace:
        """;

    private const string Fabric = """
        ---- Minecraft Crash Report ----
        Description: Initializing game

        java.lang.RuntimeException: Mixin transformation of net.minecraft.class_310 failed
        Caused by: org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: An unexpected critical error was encountered
        Caused by: org.spongepowered.asm.mixin.throwables.MixinApplyError: Mixin [sodium.mixins.json:core.MinecraftClientMixin] from phase [DEFAULT] in config [sodium.mixins.json] FAILED during APPLY
        Mixin apply for mod sodium failed sodium.mixins.json:core.MinecraftClientMixin
        """;

    private const string Jvm = """
        #
        # A fatal error has been detected by the Java Runtime Environment:
        #
        #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffb1a2b3c4d, pid=1234, tid=5678
        #
        # JRE version: OpenJDK Runtime Environment Microsoft-123 (21.0.7+6) (build 21.0.7+6-LTS)
        # Problematic frame:
        # C  [atio6axx.dll+0x1b3c4d]
        #
        """;

    [Fact]
    public void Vanilla_report_yields_description_exception_and_deepest_cause()
    {
        var s = CrashReports.Summarize(Vanilla, false);
        Assert.Equal("Minecraft crashed: Rendering overlay", s.Title);
        Assert.StartsWith("java.lang.NullPointerException", s.Exception);
        Assert.Equal("java.lang.OutOfMemoryError: Java heap space", s.RootCause);
        Assert.Contains("ran out of memory", s.Hint);
        Assert.Empty(s.SuspectedMods);
    }

    [Fact]
    public void NeoForge_suspected_mods_are_listed() =>
        Assert.Equal(["Sodium (sodium), Version: 0.6.0", "Iris (iris), Version: 1.8.0"], CrashReports.Summarize(NeoForge, false).SuspectedMods);

    [Fact]
    public void Fabric_mixin_failure_names_the_mod_and_gives_a_hint()
    {
        var s = CrashReports.Summarize(Fabric, false);
        Assert.Contains("sodium", s.SuspectedMods);
        Assert.Contains("mixin", s.Hint);
    }

    [Fact]
    public void Jvm_crash_in_graphics_driver_suggests_driver_update()
    {
        var s = CrashReports.Summarize(Jvm, true);
        Assert.StartsWith("EXCEPTION_ACCESS_VIOLATION", s.Exception);
        Assert.Equal("C  [atio6axx.dll+0x1b3c4d]", s.RootCause);
        Assert.Contains("graphics driver", s.Hint);
    }

    [Fact]
    public void Files_are_listed_newest_first_and_read_with_redaction()
    {
        var dir = Directory.CreateTempSubdirectory("ninja-crash-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "crash-reports"));
            var older = Path.Combine(dir, "crash-reports", "crash-1-client.txt");
            File.WriteAllText(older, "Description: old");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(Path.Combine(dir, "hs_err_pid42.log"), "--accessToken eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sig123456");
            var files = CrashReports.List(dir);
            Assert.Equal(["hs_err_pid42.log", "crash-1-client.txt"], files.Select(f => f.Name));
            Assert.True(files[0].IsJvmCrash);
            Assert.DoesNotContain("eyJhbGci", CrashReports.ReadText(files[0]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Exception_without_message_is_still_found()
    {
        var s = CrashReports.Summarize("""
            ---- Minecraft Crash Report ----
            Description: Unexpected error

            java.lang.NullPointerException
            	at a.b(c.java:1)

            -- Head --
            Thread: Render thread
            """, false);
        Assert.Equal("java.lang.NullPointerException", s.Exception);
    }

    [Fact]
    public void Missing_description_still_gives_a_title() =>
        Assert.Equal("Minecraft crashed", CrashReports.Summarize("garbage", false).Title);
}
