/*
 License: MIT - see license file at https://github.com/warrenfalk/auto-native-import/blob/master/LICENSE
 Author: Warren Falk <warren@warrenfalk.com>
 */
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using Transitional;

namespace NativeImport
{
    public static class Auto
    {
        public static string LoadedPath { get; internal set; }
        /// <summary>
        /// Imports the library by name (without extensions) locating it based on platform.
        /// 
        /// Use <code>suppressUnload</code> to prevent the dll from unloading at finalization,
        /// which can be useful if you need to call the imported functions in finalizers of
        /// other instances and can't predict in which order the finalization will occur
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name"></param>
        /// <param name="suppressUnload">true to prevent unloading on finalization</param>
        /// <returns></returns>
        public static T Import<T>(string name, string version, bool suppressUnload = false) where T : class
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Importers.Import<T>(Importers.Windows, name, version, suppressUnload);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Importers.Import<T>(Importers.Posix, name, version, suppressUnload);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Importers.Import<T>(Importers.Posix, name, version, suppressUnload);
            else
                return Importers.Import<T>(Importers.Windows, name, version, suppressUnload);
        }
    }

    public interface INativeLibImporter
    {
        IntPtr LoadLibrary(string name);
        IntPtr GetProcAddress(IntPtr lib, string entryPoint);
        void FreeLibrary(IntPtr lib);
        string Translate(string name, string suffix = "");
        TDelegate GetDelegate<TDelegate>(IntPtr lib, string entryPoint) where TDelegate : class;
    }

    public static class Importers
    {
        private static Lazy<WindowsImporter> WindowsShared { get; } = new Lazy<WindowsImporter>(() => new WindowsImporter());
        private static Lazy<PosixImporter> PosixShared { get; } = new Lazy<PosixImporter>(() => new PosixImporter());

        public static INativeLibImporter Windows => WindowsShared.Value;
        public static INativeLibImporter Posix => PosixShared.Value;

        static TDelegate GetDelegate<TDelegate>(INativeLibImporter importer, IntPtr lib, string entryPoint) where TDelegate : class
        {
            IntPtr procAddress = importer.GetProcAddress(lib, entryPoint);
            if (procAddress == IntPtr.Zero)
                return null;
            return CurrentFramework.GetDelegateForFunctionPointer<TDelegate>(procAddress);
        }

        private class WindowsImporter : INativeLibImporter
        {
            [DllImport("kernel32.dll", EntryPoint = "LoadLibrary", SetLastError = true)]
            public static extern IntPtr WinLoadLibrary(string dllToLoad);

            [DllImport("kernel32.dll", EntryPoint = "GetProcAddress")]
            public static extern IntPtr WinGetProcAddress(IntPtr hModule, string procedureName);

            [DllImport("kernel32.dll", EntryPoint = "FreeLibrary")]
            public static extern bool WinFreeLibrary(IntPtr hModule);

            public IntPtr LoadLibrary(string path)
            {
                var result = WinLoadLibrary(path);
                if (result == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return result;
            }

            public IntPtr GetProcAddress(IntPtr lib, string entryPoint)
            {
                var address = WinGetProcAddress(lib, entryPoint);
                return address;
            }

            public TDelegate GetDelegate<TDelegate>(IntPtr lib, string entryPoint) where TDelegate : class
                => Importers.GetDelegate<TDelegate>(this, lib, entryPoint);

            public void FreeLibrary(IntPtr lib)
            {
                WinFreeLibrary(lib);
            }

            public string Translate(string name, string suffix = "")
            {
                return name + suffix + ".dll";
            }
        }

        private class PosixImporter : INativeLibImporter
        {
            public string LibraryExtension { get; }

            [DllImport("libdl", EntryPoint = "dlopen")]
            private static extern IntPtr dlopen(string fileName, int flags);

            [DllImport("libdl", EntryPoint = "dlsym")]
            private static extern IntPtr dlsym(IntPtr handle, string symbol);

            [DllImport("libdl", EntryPoint = "dlclose")]
            private static extern int dlclose(IntPtr handle);

            [DllImport("libdl", EntryPoint = "dlerror")]
            private static extern IntPtr dlerror();

            [DllImport("libc", EntryPoint = "uname")]
            private static extern int uname(IntPtr buf);


            [DllImport("libdl.so.2", EntryPoint = "dlopen")]
            private static extern IntPtr so2_dlopen(string fileName, int flags);

            [DllImport("libdl.so.2", EntryPoint = "dlsym")]
            private static extern IntPtr so2_dlsym(IntPtr handle, string symbol);

            [DllImport("libdl.so.2", EntryPoint = "dlclose")]
            private static extern int so2_dlclose(IntPtr handle);

            [DllImport("libdl.so.2", EntryPoint = "dlerror")]
            private static extern IntPtr so2_dlerror();

            private static bool _use_so2 = false;

            public PosixImporter()
            {
                var platform = GetPlatform();
                LibraryExtension = platform.StartsWith("Darwin") ? "dylib" : "so";
            }

            static string GetPlatform()
            {
                IntPtr buf = IntPtr.Zero;
                try
                {
                    buf = Marshal.AllocHGlobal(8192);
                    return (0 == uname(buf)) ? Marshal.PtrToStringAnsi(buf) : "Unknown";
                }
                catch(Exception E) when (E is not DllNotFoundException)
                {
                    return "Unknown";
                }
                finally
                {
                    if (buf != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
            }

            public IntPtr LoadLibrary(string path)
            {
                if (_use_so2) return so2_LoadLibrary(path);

                try
                {
                    dlerror();
                    var lib = dlopen(path, 2);
                    var errPtr = dlerror();
                    if (errPtr != IntPtr.Zero)
                    {
                        throw new NativeLoadException("dlopen: " + Marshal.PtrToStringAnsi(errPtr), null);
                    }
                    return lib;
                }
                catch (DllNotFoundException)
                {
                    return so2_LoadLibrary(path);
                }
            }

            private static IntPtr so2_LoadLibrary(string path)
            {
                _use_so2 = true;
                so2_dlerror();
                var lib = so2_dlopen(path, 2);
                var errPtr = so2_dlerror();
                if (errPtr != IntPtr.Zero)
                {
                    throw new NativeLoadException("so2_dlopen: " + Marshal.PtrToStringAnsi(errPtr), null);
                }
                return lib;
            }

            public IntPtr GetProcAddress(IntPtr lib, string entryPoint)
            {
                if (_use_so2) return so2_GetProcAddress(lib, entryPoint);
                try
                {
                    dlerror();
                    IntPtr address = dlsym(lib, entryPoint);
                    return address;
                }
                catch (DllNotFoundException)
                {
                    return so2_GetProcAddress(lib, entryPoint);
                }
            }

            private static IntPtr so2_GetProcAddress(IntPtr lib, string entryPoint)
            {
                _use_so2 = true;
                so2_dlerror();
                IntPtr address = so2_dlsym(lib, entryPoint);
                return address;
            }

            public TDelegate GetDelegate<TDelegate>(IntPtr lib, string entryPoint) where TDelegate : class
                => Importers.GetDelegate<TDelegate>(this, lib, entryPoint);

            public void FreeLibrary(IntPtr lib)
            {
                if (_use_so2)
                {
                    so2_FreeLibrary(lib);
                    return;
                }

                try
                {
                    dlclose(lib);
                }
                catch (DllNotFoundException)
                {
                    so2_FreeLibrary(lib);
                    return;
                }
            }

            private static void so2_FreeLibrary(IntPtr lib)
            {
                _use_so2 = true;
                so2_dlclose(lib);
            }

            public string Translate(string name, string suffix = "")
            {
                return "lib" + name + suffix + "." + LibraryExtension;
            }
        }


        public static string GetArchName(Architecture arch)
        {
            switch (arch)
            {
                case Architecture.X86:
                    return "i386";
                case Architecture.X64:
                    return "amd64";
                default:
                    return arch.ToString().ToLower();
            }
        }

        private static string GetRuntimeId(Architecture processArchitecture)
        {
            var arch = processArchitecture.ToString().ToLower();
            var os =
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                "win";
            return $"{os}-{arch}";
        }



        public static T Import<T>(INativeLibImporter importer, string libName, string version, bool suppressUnload) where T : class
        {
            if (typeof(T) == typeof(RocksDbSharp.Native))
            {
                return (T)(object)ImportRocksDbNative(importer, libName, version, suppressUnload);
            }

            throw new PlatformNotSupportedException($"Type {typeof(T).FullName} is not supported by this importer. Only RocksDbSharp.Native is supported.");
        }

        private static RocksDbSharp.Native ImportRocksDbNative(INativeLibImporter importer, string libName, string version, bool suppressUnload)
        {
            return ImportFromSearchPaths(
                importer,
                libName,
                version,
                lib => new RocksDbSharp.NativeStaticImport(importer, lib, suppressUnload)
            );
        }

        private static T ImportFromSearchPaths<T>(INativeLibImporter importer, string libName, string version, Func<IntPtr, T> construct) where T : class
        {
            var subdir = GetArchName(RuntimeInformation.ProcessArchitecture);
            var runtimeId = GetRuntimeId(RuntimeInformation.ProcessArchitecture);

            var versionParts = version.Split('.');
            var names = versionParts.Select((p, i) => libName + "-" + string.Join(".", versionParts.Take(i + 1)))
                .Reverse()
                .Concat(Enumerable.Repeat(libName, 1));

            // try to load locally
            var paths = new[]
            {
                Path.Combine("runtimes", runtimeId, "native"),
                Path.Combine("native", subdir),
                "native",
                subdir,
                "",
            };

            var basePaths = new HashSet<string>();
            
            //Some paths might throw NotSupportedException when used from single file deployment. We could test for that, but we can also just ignore it

            try { basePaths.Add(Directory.GetCurrentDirectory()); } catch { /* Ignore */ }
            try { basePaths.Add(Path.GetDirectoryName(UriToPath(AppContext.BaseDirectory))); } catch { /* Ignore */ }
            try { basePaths.Add(Path.GetDirectoryName(UriToPath(Transitional.CurrentFramework.GetBaseDirectory()))); } catch { /* Ignore */ }
#if !NET8_0_OR_GREATER
            try { basePaths.Add(Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)); } catch { /* Ignore */ }
            try { basePaths.Add(Path.GetDirectoryName(typeof(PosixImporter).GetTypeInfo().Assembly.Location)); } catch { /* Ignore */ }
#endif

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                basePaths.Add("/opt/homebrew/lib");
            }


            var baseSearchPaths = basePaths.Where(p => p is object)
                                           .SelectMany(basePath => paths.SelectMany(path => names.Select(n => Path.Combine(basePath, path, importer.Translate(n)))))
                                           .Concat(names.Select(n => importer.Translate(n)));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                //Try also to load the -jemalloc variants
                baseSearchPaths = basePaths.Where(p => p is object)
                                           .SelectMany(basePath =>
                                                       paths.SelectMany(path => names.Select(n => Path.Combine(basePath, path, importer.Translate(n, "-jemalloc"))))
                                           .Concat(names.Select(n => importer.Translate(n, "-jemalloc"))))
                                           .Concat(baseSearchPaths)
                                           .ToArray();

                //Try also to load the -musl variants
                baseSearchPaths = baseSearchPaths.Concat(basePaths.Where(p => p is object)
                                                                  .SelectMany(basePath =>
                                                                                paths.SelectMany(path => names.Select(n => Path.Combine(basePath, path, importer.Translate(n, "-musl"))))
                                                                  .Concat(names.Select(n => importer.Translate(n, "-musl")))));
            }

            var search = baseSearchPaths.Select(path => new SearchPath { Path = path }).ToArray();

            foreach (var spec in search)
            {
                IntPtr lib = IntPtr.Zero;
                try
                {
                    lib = importer.LoadLibrary(spec.Path);
                    if (lib == IntPtr.Zero)
                        throw new NativeLoadException("LoadLibrary returned 0", null);
                    Auto.LoadedPath = spec.Path;
                }
                catch (TargetInvocationException tie)
                {
                    spec.Error = tie.InnerException;
                    continue;
                }
                catch (Exception e)
                {
                    spec.Error = e;
                    continue;
                }

                try
                {
                    var t = construct(lib);
                    if (t is null)
                    {
                        throw new NativeLoadException($"Loader returned null for type {typeof(T).FullName}", null);
                    }
                    return t;
                }
                catch (TargetInvocationException tie)
                {
                    spec.Error = tie.InnerException ?? tie;
                    TryFreeLibrary(importer, lib);
                    continue;
                }
                catch (Exception e)
                {
                    spec.Error = e;
                    TryFreeLibrary(importer, lib);
                    continue;
                }
            }

            throw new NativeLoadException("Unable to locate rocksdb native library, either install it, or use RocksDbNative nuget package\nSearched:\n" + string.Join("\n", search.Select(FormatSearchError)), null);
        }

        private static string FormatSearchError(SearchPath searchPath)
        {
            var error = searchPath.Error;
            if (error is null)
            {
                return $"{searchPath.Path}: (UnknownError) Unknown";
            }
            return $"{searchPath.Path}: ({error.GetType().Name}) {error.Message}";
        }

        private static void TryFreeLibrary(INativeLibImporter importer, IntPtr lib)
        {
            if (lib == IntPtr.Zero)
            {
                return;
            }

            try
            {
                importer.FreeLibrary(lib);
            }
            catch
            {
            }
        }

        private static string UriToPath(string uriString)
        {
            if (uriString == null || !Uri.IsWellFormedUriString(uriString, UriKind.RelativeOrAbsolute))
                return null;
            var uri = new Uri(uriString);
            return uri.LocalPath;
        }

        private class SearchPath
        {
            public string Path { get; set; }
            public Exception Error { get; set; }
        }

    }

    public class NativeFunctionMissingException : Exception
    {
        public NativeFunctionMissingException()
            : base("Failed to find entry point")
        {
        }

        public NativeFunctionMissingException(string entryPoint)
            : base($"Failed to find entry point for {entryPoint}", null)
        {
        }
    }

    public class NativeLoadException : Exception
    {
        public NativeLoadException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
