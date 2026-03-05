using System;
using System.IO;
using RocksDbSharp;

internal static class Program
{
    private static int Main()
    {
        var testId = Guid.NewGuid().ToString("N");
        var basePath = Path.Combine(Path.GetTempPath(), "RocksDbSharp.AotRuntimeSmoke", testId);
        Directory.CreateDirectory(basePath);

        try
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using var db = RocksDb.Open(options, basePath);

            const string key = "aot-key";
            const string value = "aot-value";
            db.Put(key, value);

            var loaded = db.Get(key);
            if (!string.Equals(loaded, value, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"AOT runtime smoke test failed: expected '{value}', got '{loaded ?? "<null>"}'.");
                return 1;
            }

            db.Remove(key);
            var afterDelete = db.Get(key);
            if (afterDelete is not null)
            {
                Console.Error.WriteLine("AOT runtime smoke test failed: key still present after delete.");
                return 1;
            }

            Console.WriteLine($"Native library loaded from: {NativeImport.Auto.LoadedPath}");
            Console.WriteLine("AOT_RUNTIME_SMOKE_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("AOT runtime smoke test threw an exception:");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(basePath))
                {
                    Directory.Delete(basePath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
