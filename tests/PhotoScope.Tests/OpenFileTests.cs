using System.IO.Pipes;
using System.Text;
using PhotoScope.Services;

namespace PhotoScope.Tests;

public sealed class OpenFileTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    // ------------------------------------------------------------------ command line

    [Fact]
    public void Finds_the_file_argument_with_spaces_and_unicode()
    {
        var path = _tmp.Sub("Holiday Photos", "Café – été 2024 🌅.jpg");
        Assert.Equal(path, CommandLine.FindFileArgument([path]));
        Assert.Equal(path, CommandLine.FindFileArgument(["\"" + path + "\""]));   // stray quotes
        Assert.Equal(path, CommandLine.FindFileArgument(["  " + path + "  "]));
    }

    [Fact]
    public void Skips_flags_and_debug_arguments()
    {
        var path = _tmp.Sub("a.jpg");
        Assert.Equal(path, CommandLine.FindFileArgument(["--something", path]));
        Assert.Equal(path, CommandLine.FindFileArgument(["--debug-import", @"C:\photos", "copy", path]));
        Assert.Null(CommandLine.FindFileArgument(["--debug-import", @"C:\photos"]));
        Assert.Null(CommandLine.FindFileArgument([]));
        Assert.Null(CommandLine.FindFileArgument(["", "  "]));
    }

    [Fact]
    public void Normalizes_relative_paths_and_file_uris()
    {
        Assert.Equal(Path.Combine(_tmp.Path, "x.png"), CommandLine.Normalize("x.png", _tmp.Path));
        var uri = new Uri(_tmp.Sub("my photo.jpg")).AbsoluteUri;           // file:///...my%20photo.jpg
        Assert.Equal(_tmp.Sub("my photo.jpg"), CommandLine.Normalize(uri));
    }

    [Fact]
    public void Check_accepts_a_real_image_including_unicode_names()
    {
        var path = TestImages.Jpeg(_tmp.Sub("Ünïcödé 写真.jpg"));
        var check = CommandLine.Check(path);
        Assert.True(check.Ok, check.Message);
        Assert.Equal(path, check.Path);
    }

    [Fact]
    public void Check_reports_missing_folder_and_unsupported_files_kindly()
    {
        var missing = CommandLine.Check(_tmp.Sub("gone.jpg"));
        Assert.Equal(OpenProblem.NotFound, missing.Problem);
        Assert.Contains("can't be found", missing.Message);

        Assert.Equal(OpenProblem.IsFolder, CommandLine.Check(_tmp.Path).Problem);

        var txt = _tmp.Sub("notes.txt");
        File.WriteAllText(txt, "x");
        var unsupported = CommandLine.Check(txt);
        Assert.Equal(OpenProblem.Unsupported, unsupported.Problem);
        Assert.Contains("notes.txt", unsupported.Message);

        Assert.Equal(OpenProblem.Empty, CommandLine.Check("").Problem);
        Assert.Equal(OpenProblem.Invalid, CommandLine.Check("C:\\bad\0name.jpg").Problem);
        Assert.All(new[] { missing, unsupported }, c => Assert.DoesNotContain("   at ", c.Message));
    }

    [Fact]
    public void Check_handles_long_paths()
    {
        var dir = _tmp.Path;
        while (dir.Length < 300) dir = Path.Combine(dir, new string('d', 40));
        Directory.CreateDirectory(dir);
        var path = TestImages.Jpeg(Path.Combine(dir, "long path photo.jpg"));
        Assert.True(path.Length > 260);
        Assert.True(CommandLine.Check(path).Ok);
        Assert.Contains(path, FolderListing.ListImages(dir));
        Assert.Equal(path, CommandLine.FindFileArgument([path]));
    }

    // ------------------------------------------------------------------ folder listing

    [Fact]
    public void Folder_listing_sorts_like_explorer_and_skips_hidden_and_other_files()
    {
        foreach (var n in new[] { "IMG_10.jpg", "IMG_2.jpg", "img_1.PNG", "b.gif", "notes.txt" })
            File.WriteAllText(_tmp.Sub(n), "x");
        var hidden = _tmp.Sub("hidden.jpg");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.CreateDirectory(_tmp.Sub("sub"));
        File.WriteAllText(_tmp.Sub("sub", "deeper.jpg"), "x");

        var names = FolderListing.ListImages(_tmp.Path).Select(Path.GetFileName).ToList();
        Assert.Equal(["b.gif", "img_1.PNG", "IMG_2.jpg", "IMG_10.jpg"], names);

        // The file that was opened is always included, even if hidden.
        Assert.Contains(hidden, FolderListing.ListImages(_tmp.Path, hidden));
    }

    // ------------------------------------------------------------------ single instance

    static string UniqueId() => "PhotoScopeTest-" + Guid.NewGuid().ToString("N")[..12];

    static T OnThread<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.Start();
        t.Join();
        if (error != null) throw new Exception("thread failed", error);
        return result;
    }

    [Fact]
    public void Id_is_stable_and_depends_on_the_data_folder()
    {
        Assert.Equal(SingleInstance.IdFor(@"C:\data\a"), SingleInstance.IdFor(@"c:\DATA\a"));
        Assert.NotEqual(SingleInstance.IdFor(@"C:\data\a"), SingleInstance.IdFor(@"C:\data\b"));
    }

    [Fact]
    public void Second_launch_is_not_primary_and_hands_its_file_to_the_first()
    {
        var id = UniqueId();
        using var first = SingleInstance.Acquire(id);
        Assert.True(first.IsPrimary);

        var received = new List<InstanceMessage>();
        var got = new ManualResetEventSlim();
        first.StartServer(m => { lock (received) received.Add(m); got.Set(); return Task.FromResult(true); });

        // A second process is simulated with another thread (mutex ownership is per thread).
        var secondIsPrimary = OnThread(() =>
        {
            using var second = SingleInstance.Acquire(id);
            return second.IsPrimary;
        });
        Assert.False(secondIsPrimary);

        var path = _tmp.Sub("Holiday Photos", "Café 写真 🌅.jpg");
        Assert.True(SingleInstance.TrySend(id, new InstanceMessage(InstanceMessage.Open, path), TimeSpan.FromSeconds(5)));
        Assert.True(got.Wait(TimeSpan.FromSeconds(5)));
        lock (received) Assert.Equal(new InstanceMessage(InstanceMessage.Open, path), received.Single());

        got.Reset();
        Assert.True(SingleInstance.TrySend(id, new InstanceMessage(InstanceMessage.Activate), TimeSpan.FromSeconds(5)));
        Assert.True(got.Wait(TimeSpan.FromSeconds(5)));
        lock (received) Assert.Equal(InstanceMessage.Activate, received[^1].Command);
    }

    [Fact]
    public void Garbage_on_the_pipe_is_ignored_and_the_server_keeps_working()
    {
        var id = UniqueId();
        using var first = SingleInstance.Acquire(id);
        var received = new List<InstanceMessage>();
        var got = new ManualResetEventSlim();
        first.StartServer(m => { lock (received) received.Add(m); got.Set(); return Task.FromResult(true); });

        void SendRaw(byte[] bytes)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", id, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                    client.Connect(1000);
                    client.Write(bytes);
                    client.Flush();
                    Thread.Sleep(100);
                    return;
                }
                catch (IOException) { Thread.Sleep(100); }
                catch (TimeoutException) { Thread.Sleep(100); }
            }
            Assert.Fail("couldn't connect to the pipe to send test data");
        }

        SendRaw([0xFF, 0xFF, 0xFF, 0x7F]);                                        // absurd length
        var junk = Encoding.UTF8.GetBytes("{not json");
        SendRaw([.. BitConverter.GetBytes(junk.Length), .. junk]);                 // malformed JSON
        var evil = Encoding.UTF8.GetBytes("{\"Command\":\"delete\",\"Path\":\"C:\\\\x\"}");
        SendRaw([.. BitConverter.GetBytes(evil.Length), .. evil]);                 // unknown command

        Assert.False(got.Wait(TimeSpan.FromMilliseconds(300)));
        Assert.True(SingleInstance.TrySend(id, new InstanceMessage(InstanceMessage.Activate), TimeSpan.FromSeconds(5)));
        Assert.True(got.Wait(TimeSpan.FromSeconds(5)));
        lock (received) Assert.Equal(InstanceMessage.Activate, received.Single().Command);
    }

    [Fact]
    public void A_handoff_the_running_copy_cannot_take_is_reported_as_not_delivered()
    {
        var id = UniqueId();
        using var first = SingleInstance.Acquire(id);
        first.StartServer(_ => Task.FromResult(false)); // e.g. shutting down, or its UI didn't answer
        Assert.False(SingleInstance.TrySend(id, new InstanceMessage(InstanceMessage.Activate), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Odd_paths_from_the_shell_are_handled()
    {
        var plain = _tmp.Sub("photo.jpg");
        Assert.Equal(plain, CommandLine.Normalize(@"\\?\" + plain));                          // long-path prefix stripped
        Assert.Equal(@"\\server\share\a.jpg", CommandLine.Normalize(@"\\?\UNC\server\share\a.jpg"));
        var hashName = _tmp.Sub("photo #1.jpg");
        Assert.Equal(hashName, CommandLine.Normalize(new Uri(hashName).AbsoluteUri));        // '#' isn't lost
        // A trailing dot would make Windows open a different file: refused.
        File.WriteAllText(plain, "x");
        Assert.Equal(OpenProblem.Invalid, CommandLine.Check(CommandLine.Normalize(plain + ".")).Problem);
    }

    [Fact]
    public void When_the_first_copy_exits_the_next_launch_becomes_primary()
    {
        var id = UniqueId();
        var first = SingleInstance.Acquire(id);
        Assert.True(first.IsPrimary);
        first.Dispose();
        Assert.True(OnThread(() =>
        {
            using var next = SingleInstance.Acquire(id);
            return next.IsPrimary;
        }));
    }

    [Fact]
    public void Sending_with_nobody_listening_gives_up_quickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SingleInstance.TrySend(UniqueId(), new InstanceMessage(InstanceMessage.Activate), TimeSpan.FromSeconds(1)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));
    }

    // ------------------------------------------------------------------ Open with registration plan

    [Fact]
    public void Open_with_registration_is_per_user_and_quotes_the_command()
    {
        var exe = @"C:\Program Files\Photo Scope\PhotoScope.exe";
        var plan = FileAssociations.Plan(exe);
        Assert.All(plan, e => Assert.True(
            e.Key.StartsWith(@"Software\Classes\", StringComparison.Ordinal) ||
            e.Key.StartsWith(@"Software\PhotoScope\", StringComparison.Ordinal) ||
            e.Key == @"Software\RegisteredApplications", e.Key));
        Assert.Contains(plan, e => e.Value == $"\"{exe}\" \"%1\"");
        foreach (var ext in ImageDecoder.Extensions)
            Assert.Contains(plan, e => e.Key == $@"Software\Classes\{ext}\OpenWithProgids" && e.ValueName == FileAssociations.ProgId);
        // Never touches an extension's default value (that would change the default app).
        Assert.DoesNotContain(plan, e => e.Key.StartsWith(@"Software\Classes\.", StringComparison.Ordinal) && !e.Key.EndsWith("OpenWithProgids"));
    }
}
