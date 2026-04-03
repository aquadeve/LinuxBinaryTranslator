// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Rootfs manager for mounting Linux distribution filesystem trees
// into the virtual file system. Supports loading rootfs from a
// StorageFolder (UWP local storage / Xbox One app data) and
// populating the VFS with the full directory hierarchy.
//
// Typical rootfs layout:
//   /bin/bash, /usr/bin/*, /lib/x86_64-linux-gnu/*.so
//   /etc/passwd, /etc/group, /etc/hostname
//   /proc, /dev, /tmp (virtual, handled by VFS)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LinuxBinaryTranslator.FileSystem
{
    /// <summary>
    /// Detected Linux distribution type for rootfs configuration.
    /// </summary>
    public enum DistroType
    {
        Unknown,
        Ubuntu,
        Debian,
        ArchLinux,
        Alpine,
        Fedora,
        CentOS,
    }

    /// <summary>
    /// Information about a loaded rootfs.
    /// </summary>
    public sealed class RootfsInfo
    {
        public DistroType Distro { get; set; }
        public string DistroName { get; set; } = "Unknown Linux";
        public string Version { get; set; } = "";
        public string ShellPath { get; set; } = "/bin/sh";
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
    }

    /// <summary>
    /// Manages loading and mounting a Linux rootfs directory into the
    /// virtual file system. The rootfs can be extracted from a tar.gz
    /// or loaded from an already-extracted directory on UWP local storage.
    ///
    /// On Xbox One, rootfs files are stored in the app's LocalFolder
    /// (accessible via Windows.Storage.ApplicationData.Current.LocalFolder).
    /// </summary>
    public sealed class RootfsManager
    {
        private readonly VirtualFileSystem _vfs;
        private readonly Action<string> _logger;

        // In-memory filesystem tree for the rootfs
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _symlinks = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _permissions = new Dictionary<string, uint>(StringComparer.Ordinal);

        // Root prefix for chroot-style isolation
        private string _rootPrefix = "";

        public RootfsInfo? Info { get; private set; }

        public RootfsManager(VirtualFileSystem vfs, Action<string>? logger = null)
        {
            _vfs = vfs;
            _logger = logger ?? (_ => { });
        }

        /// <summary>
        /// Load a rootfs from a flat dictionary of path → content mappings.
        /// This is the primary loading path for UWP where files are read
        /// from StorageFolder into byte arrays.
        /// </summary>
        public RootfsInfo LoadFromFileMap(Dictionary<string, byte[]> fileMap)
        {
            _files.Clear();
            _directories.Clear();
            _symlinks.Clear();
            _permissions.Clear();

            // Ensure root directories exist
            _directories.Add("/");
            _directories.Add("/bin");
            _directories.Add("/sbin");
            _directories.Add("/usr");
            _directories.Add("/usr/bin");
            _directories.Add("/usr/sbin");
            _directories.Add("/usr/lib");
            _directories.Add("/usr/lib64");
            _directories.Add("/lib");
            _directories.Add("/lib64");
            _directories.Add("/etc");
            _directories.Add("/var");
            _directories.Add("/var/tmp");
            _directories.Add("/var/log");
            _directories.Add("/tmp");
            _directories.Add("/home");
            _directories.Add("/root");
            _directories.Add("/dev");
            _directories.Add("/proc");
            _directories.Add("/sys");
            _directories.Add("/run");
            _directories.Add("/opt");

            long totalSize = 0;

            foreach (var kvp in fileMap)
            {
                string path = NormalizePath(kvp.Key);
                if (string.IsNullOrEmpty(path)) continue;

                _files[path] = kvp.Value;
                totalSize += kvp.Value.Length;

                // Register all parent directories
                RegisterParentDirectories(path);
            }

            // Mount all files and directories into the VFS
            MountIntoVfs();

            // Detect the distribution
            var info = DetectDistro();
            info.FileCount = _files.Count;
            info.TotalSize = totalSize;
            Info = info;

            _logger($"Rootfs loaded: {info.DistroName} {info.Version}, " +
                    $"{info.FileCount} files, {info.TotalSize / 1024}KB, " +
                    $"shell={info.ShellPath}");

            return info;
        }

        /// <summary>
        /// Add a single file to the rootfs. Used for incremental loading
        /// from UWP StorageFolder enumeration.
        /// </summary>
        public void AddFile(string path, byte[] data)
        {
            path = NormalizePath(path);
            if (string.IsNullOrEmpty(path)) return;

            _files[path] = data;
            RegisterParentDirectories(path);

            // Mount into VFS immediately
            _vfs.Mount(path, () => new MemoryFile((byte[])data.Clone()));
        }

        /// <summary>
        /// Add a symbolic link to the rootfs.
        /// </summary>
        public void AddSymlink(string path, string target)
        {
            path = NormalizePath(path);
            if (string.IsNullOrEmpty(path)) return;

            _symlinks[path] = target;
            RegisterParentDirectories(path);

            // For symlinks, resolve the target and mount the target file
            string resolvedTarget = ResolveSymlink(path, target);
            if (_files.TryGetValue(resolvedTarget, out var data))
            {
                _vfs.Mount(path, () => new MemoryFile((byte[])data.Clone()));
            }
        }

        /// <summary>
        /// Add a directory entry to the rootfs.
        /// </summary>
        public void AddDirectory(string path)
        {
            path = NormalizePath(path);
            if (string.IsNullOrEmpty(path)) return;
            _directories.Add(path);
            RegisterParentDirectories(path);
        }

        /// <summary>
        /// Check whether a path exists in the rootfs (file, directory, or symlink).
        /// </summary>
        public bool Exists(string path)
        {
            path = NormalizePath(path);
            return _files.ContainsKey(path) ||
                   _directories.Contains(path) ||
                   _symlinks.ContainsKey(path);
        }

        /// <summary>
        /// Check if a path is a directory.
        /// </summary>
        public bool IsDirectory(string path)
        {
            path = NormalizePath(path);
            return _directories.Contains(path);
        }

        /// <summary>
        /// Read file contents from the rootfs.
        /// </summary>
        public byte[]? ReadFile(string path)
        {
            path = NormalizePath(path);
            return TryResolveFile(path, 0);
        }

        /// <summary>
        /// List files/directories in a directory.
        /// </summary>
        public List<string> ListDirectory(string path)
        {
            path = NormalizePath(path);
            if (!path.EndsWith("/")) path += "/";

            var entries = new List<string>();
            var seen = new HashSet<string>();

            // List immediate children (files)
            foreach (var file in _files.Keys)
            {
                if (file.StartsWith(path) && file.Length > path.Length)
                {
                    string relative = file.Substring(path.Length);
                    int slashIdx = relative.IndexOf('/');
                    string entry = slashIdx >= 0 ? relative.Substring(0, slashIdx) : relative;
                    if (seen.Add(entry))
                        entries.Add(entry);
                }
            }

            // List immediate children (directories)
            foreach (var dir in _directories)
            {
                if (dir.StartsWith(path) && dir.Length > path.Length)
                {
                    string relative = dir.Substring(path.Length);
                    int slashIdx = relative.IndexOf('/');
                    string entry = slashIdx >= 0 ? relative.Substring(0, slashIdx) : relative;
                    if (seen.Add(entry))
                        entries.Add(entry);
                }
            }

            return entries;
        }

        /// <summary>
        /// Resolve a symlink target to an absolute path.
        /// </summary>
        public string ResolveSymlink(string linkPath, string target)
        {
            if (target.StartsWith("/"))
                return NormalizePath(target);

            // Relative symlink — resolve relative to link's parent directory
            int lastSlash = linkPath.LastIndexOf('/');
            string parent = lastSlash > 0 ? linkPath.Substring(0, lastSlash) : "/";
            return NormalizePath(parent + "/" + target);
        }

        /// <summary>
        /// Read a symlink's target string, if it exists.
        /// </summary>
        public string? ReadLink(string path)
        {
            path = NormalizePath(path);
            return _symlinks.TryGetValue(path, out string? target) ? target : null;
        }

        private byte[]? TryResolveFile(string path, int depth)
        {
            if (depth > 16)
                return null;

            if (_files.TryGetValue(path, out var data))
                return data;

            if (_symlinks.TryGetValue(path, out string? target))
            {
                string resolved = ResolveSymlink(path, target);
                return TryResolveFile(resolved, depth + 1);
            }

            foreach (string fallbackPath in GetFallbackPaths(path))
            {
                if (fallbackPath == path)
                    continue;

                byte[]? fallbackData = TryResolveFile(fallbackPath, depth + 1);
                if (fallbackData != null)
                    return fallbackData;
            }

            return null;
        }

        /// <summary>
        /// Get the default shell path for the loaded distro.
        /// </summary>
        public string GetShellPath()
        {
            // Try bash first, then sh, then busybox
            if (ReadFile("/bin/bash") != null) return "/bin/bash";
            if (ReadFile("/usr/bin/bash") != null) return "/usr/bin/bash";
            if (ReadFile("/bin/sh") != null) return "/bin/sh";
            if (ReadFile("/usr/bin/sh") != null) return "/usr/bin/sh";
            if (ReadFile("/bin/busybox") != null) return "/bin/busybox";
            return "/bin/sh";
        }

        /// <summary>
        /// Get default environment variables for the loaded distro.
        /// </summary>
        public string[] GetDefaultEnvironment()
        {
            var env = new List<string>
            {
                "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "HOME=/root",
                "TERM=xterm-256color",
                "LANG=C.UTF-8",
                "USER=root",
                "LOGNAME=root",
                "HOSTNAME=localhost",
                "PS1=\\u@\\h:\\w\\$ ",
                "COLUMNS=80",
                "LINES=24",
            };

            string shell = GetShellPath();
            env.Add($"SHELL={shell}");

            // Distro-specific environment
            if (Info?.Distro == DistroType.Alpine)
            {
                env.Add("CHARSET=UTF-8");
            }

            return env.ToArray();
        }

        // === Internal helpers ===

        private void RegisterParentDirectories(string path)
        {
            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string current = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current += "/" + parts[i];
                _directories.Add(current);
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            // Ensure leading /
            if (!path.StartsWith("/"))
                path = "/" + path;

            // Remove trailing / (except root)
            while (path.Length > 1 && path.EndsWith("/"))
                path = path.Substring(0, path.Length - 1);

            // Collapse double slashes
            while (path.Contains("//"))
                path = path.Replace("//", "/");

            // Resolve . and ..
            var segments = new List<string>();
            foreach (string seg in path.Split('/'))
            {
                if (seg == "" || seg == ".") continue;
                if (seg == ".." && segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                else if (seg != "..")
                    segments.Add(seg);
            }

            return "/" + string.Join("/", segments);
        }

        private void MountIntoVfs()
        {
            // Mount all files from rootfs into the VFS
            foreach (var kvp in _files)
            {
                byte[] data = kvp.Value;
                _vfs.Mount(kvp.Key, () => new MemoryFile((byte[])data.Clone()));
            }

            // Mount symlinks (resolve to their targets)
            foreach (var kvp in _symlinks)
            {
                string target = kvp.Value;
                string linkPath = kvp.Key;
                string resolved = ResolveSymlink(linkPath, target);

                if (_files.TryGetValue(resolved, out var targetData))
                {
                    byte[] data = targetData;
                    _vfs.Mount(linkPath, () => new MemoryFile((byte[])data.Clone()));
                }
            }

            // Register directory awareness in VFS via the rootfs bridge
            _vfs.SetRootfsManager(this);

            _logger($"Mounted {_files.Count} files + {_symlinks.Count} symlinks + {_directories.Count} dirs into VFS");
        }

        private IEnumerable<string> GetFallbackPaths(string path)
        {
            string fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
                yield break;

            // UWP folder import can lose symlink metadata, so well-known linker
            // aliases like /lib64/ld-linux-*.so.2 may only exist at their real
            // target locations under /usr/lib*.
            if (path.StartsWith("/lib64/", StringComparison.Ordinal))
            {
                yield return "/usr/lib64/" + fileName;
                yield return "/usr/lib/" + fileName;
                yield return "/lib/" + fileName;
            }
            else if (path.StartsWith("/lib/", StringComparison.Ordinal))
            {
                yield return "/usr/lib/" + fileName;
                yield return "/usr/lib64/" + fileName;
                yield return "/lib64/" + fileName;
            }
            else if (path.StartsWith("/usr/lib64/", StringComparison.Ordinal))
            {
                yield return "/lib64/" + fileName;
                yield return "/usr/lib/" + fileName;
                yield return "/lib/" + fileName;
            }
            else if (path.StartsWith("/usr/lib/", StringComparison.Ordinal))
            {
                yield return "/lib/" + fileName;
                yield return "/lib64/" + fileName;
                yield return "/usr/lib64/" + fileName;
            }
        }

        private RootfsInfo DetectDistro()
        {
            var info = new RootfsInfo();

            // Try /etc/os-release (standard for systemd-based distros)
            if (_files.TryGetValue("/etc/os-release", out var osRelease))
            {
                string content = Encoding.UTF8.GetString(osRelease);
                info.DistroName = ParseOsReleaseField(content, "PRETTY_NAME") ??
                                  ParseOsReleaseField(content, "NAME") ?? "Linux";
                info.Version = ParseOsReleaseField(content, "VERSION_ID") ?? "";
                string id = ParseOsReleaseField(content, "ID") ?? "";

                info.Distro = id.ToLowerInvariant() switch
                {
                    "ubuntu" => DistroType.Ubuntu,
                    "debian" => DistroType.Debian,
                    "arch" => DistroType.ArchLinux,
                    "alpine" => DistroType.Alpine,
                    "fedora" => DistroType.Fedora,
                    "centos" => DistroType.CentOS,
                    _ => DistroType.Unknown,
                };
            }
            // Try Alpine-specific /etc/alpine-release
            else if (_files.TryGetValue("/etc/alpine-release", out var alpineRelease))
            {
                info.Distro = DistroType.Alpine;
                info.DistroName = "Alpine Linux";
                info.Version = Encoding.UTF8.GetString(alpineRelease).Trim();
            }
            // Try /etc/debian_version
            else if (_files.TryGetValue("/etc/debian_version", out var debVer))
            {
                info.Distro = DistroType.Debian;
                info.DistroName = "Debian";
                info.Version = Encoding.UTF8.GetString(debVer).Trim();
            }
            // Try /etc/arch-release
            else if (_files.ContainsKey("/etc/arch-release"))
            {
                info.Distro = DistroType.ArchLinux;
                info.DistroName = "Arch Linux";
            }

            info.ShellPath = GetShellPath();
            return info;
        }

        private static string? ParseOsReleaseField(string content, string field)
        {
            foreach (string line in content.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(field + "="))
                {
                    string value = trimmed.Substring(field.Length + 1);
                    // Remove surrounding quotes
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                        value = value.Substring(1, value.Length - 2);
                    return value;
                }
            }
            return null;
        }
    }
}
