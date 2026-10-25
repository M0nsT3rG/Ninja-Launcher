using NinjaLauncher.Infrastructure.Updates;

// Release tooling for the signed update feed. See docs/releases/RELEASING.md.
//   keygen <private.pem> <public.pem>     create a key pair (keep the private key secret, never commit it)
//   sign <feed.json>...                   write <feed.json>.sig; key from NINJA_FEED_SIGNING_KEY (PEM text)
//   verify <public.pem> <feed.json>...    check signatures; exit code 1 on any failure
try
{
    switch (args.FirstOrDefault())
    {
        case "keygen" when args.Length == 3:
            var (priv, pub) = FeedSignature.GenerateKeyPair();
            // CreateNew refuses to overwrite an existing key; on Unix the file is created owner-only (no chmod race).
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(args[1], System.Text.Encoding.ASCII, options)) writer.Write(priv);
            File.WriteAllText(args[2], pub);
            if (OperatingSystem.IsWindows()) Console.WriteLine("Note: on Windows the key file inherits the folder's permissions; keep it in your user profile or a password manager.");
            Console.WriteLine($"Wrote private key {args[1]} and public key {args[2]}.");
            return 0;

        case "sign" when args.Length >= 2:
            var key = Environment.GetEnvironmentVariable("NINJA_FEED_SIGNING_KEY");
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("NINJA_FEED_SIGNING_KEY is not set.");
            foreach (var feed in args.Skip(1))
            {
                File.WriteAllText(feed + FeedSignature.Extension, FeedSignature.Sign(File.ReadAllBytes(feed), FeedSignature.ChannelFromFileName(feed), key));
                Console.WriteLine($"Signed {Path.GetFileName(feed)}");
            }
            return 0;

        case "verify" when args.Length >= 3:
            var publicKey = File.ReadAllText(args[1]);
            var ok = true;
            foreach (var feed in args.Skip(2))
            {
                var valid = File.Exists(feed + FeedSignature.Extension) && FeedSignature.Verify(File.ReadAllBytes(feed),
                    FeedSignature.ChannelFromFileName(feed), File.ReadAllText(feed + FeedSignature.Extension), publicKey);
                Console.WriteLine($"{(valid ? "OK " : "BAD")} {Path.GetFileName(feed)}");
                ok &= valid;
            }
            return ok ? 0 : 1;

        default:
            Console.Error.WriteLine("usage: keygen <private.pem> <public.pem> | sign <feed.json>... | verify <public.pem> <feed.json>...");
            return 2;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
