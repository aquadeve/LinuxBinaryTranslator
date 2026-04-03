// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Virtual file system for the Linux binary translator.
// Implements a VFS layer that maps Linux paths to virtual file nodes,
// providing standard device files (/dev/null, /dev/zero, /dev/urandom)
// and console I/O for stdin/stdout/stderr.

using System;
using System.Collections.Generic;
using System.Text;

namespace LinuxBinaryTranslator.FileSystem
{
    /// <summary>
    /// Result of a stat() call on a virtual file.
    /// </summary>
    public sealed class VfsStatResult
    {
        public ulong Dev { get; set; }
        public ulong Ino { get; set; }
        public ulong Nlink { get; set; } = 1;
        public uint Mode { get; set; }
        public uint Uid { get; set; } = 1000;
        public uint Gid { get; set; } = 1000;
        public ulong Rdev { get; set; }
        public long Size { get; set; }
    }

    /// <summary>
    /// Interface for virtual file nodes in the VFS.
    /// </summary>
    public interface IVirtualFile
    {
        int Read(byte[] buffer, int offset, int count);
        int Write(byte[] data, int offset, int count);
        long Seek(long offset, int whence);
        VfsStatResult Stat();
        int GetFlags();
    }

    /// <summary>
    /// Virtual file backed by an in-memory byte buffer.
    /// Supports read, write, and seek operations for regular files.
    /// </summary>
    public sealed class MemoryFile : IVirtualFile
    {
        private byte[] _data;
        private int _position;
        private int _length;
        private readonly int _openFlags;

        public MemoryFile(byte[]? initialData = null, int flags = 0)
        {
            _data = initialData ?? new byte[4096];
            _length = initialData?.Length ?? 0;
            _position = 0;
            _openFlags = flags;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int available = _length - _position;
            if (available <= 0) return 0;
            int toRead = Math.Min(count, available);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public int Write(byte[] data, int offset, int count)
        {
            int needed = _position + count;
            if (needed > _data.Length)
            {
                int newSize = Math.Max(_data.Length * 2, needed);
                Array.Resize(ref _data, newSize);
            }
            Array.Copy(data, offset, _data, _position, count);
            _position += count;
            if (_position > _length)
                _length = _position;
            return count;
        }

        public long Seek(long offset, int whence)
        {
            long newPos = whence switch
            {
                0 => offset,                  // SEEK_SET
                1 => _position + offset,      // SEEK_CUR
                2 => _length + offset,        // SEEK_END
                _ => _position
            };
            _position = (int)Math.Max(0, Math.Min(newPos, int.MaxValue));
            return _position;
        }

        public VfsStatResult Stat() => new VfsStatResult
        {
            Mode = 0x8000 | 0x01A4, // S_IFREG | 0644
            Size = _length,
            Ino = (ulong)GetHashCode(),
        };

        public int GetFlags() => _openFlags;
    }

    /// <summary>
    /// Console device for stdin/stdout/stderr.
    /// Bridges Linux process I/O to the UWP terminal emulator.
    /// </summary>
    public sealed class ConsoleDevice : IVirtualFile
    {
        private readonly Func<byte[], int, int, int>? _readFunc;
        private readonly Action<byte[], int, int>? _writeFunc;
        private readonly bool _isTerminal;
        private readonly int _fd;

        public ConsoleDevice(int fd, Func<byte[], int, int, int>? readFunc, Action<byte[], int, int>? writeFunc)
        {
            _fd = fd;
            _readFunc = readFunc;
            _writeFunc = writeFunc;
            _isTerminal = true;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_readFunc != null)
                return _readFunc(buffer, offset, count);
            return 0;
        }

        public int Write(byte[] data, int offset, int count)
        {
            _writeFunc?.Invoke(data, offset, count);
            return count;
        }

        public long Seek(long offset, int whence) => -1; // Not seekable

        public VfsStatResult Stat() => new VfsStatResult
        {
            Mode = 0x2000 | 0x01B6, // S_IFCHR | 0666
            Rdev = (8UL << 8) | (ulong)_fd, // Major 8 (PTY)
            Ino = (ulong)(100 + _fd),
        };

        public int GetFlags() => 0;
        public bool IsTerminal => _isTerminal;
    }

    /// <summary>
    /// /dev/null — reads return EOF, writes are discarded.
    /// </summary>
    public sealed class DevNull : IVirtualFile
    {
        public int Read(byte[] buffer, int offset, int count) => 0;
        public int Write(byte[] data, int offset, int count) => count;
        public long Seek(long offset, int whence) => 0;
        public VfsStatResult Stat() => new VfsStatResult
        {
            Mode = 0x2000 | 0x0666,
            Rdev = (1 << 8) | 3,
            Ino = 10,
        };
        public int GetFlags() => 0;
    }

    /// <summary>
    /// /dev/zero — reads return zero bytes, writes are discarded.
    /// </summary>
    public sealed class DevZero : IVirtualFile
    {
        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        public int Write(byte[] data, int offset, int count) => count;
        public long Seek(long offset, int whence) => 0;
        public VfsStatResult Stat() => new VfsStatResult
        {
            Mode = 0x2000 | 0x0666,
            Rdev = (1 << 8) | 5,
            Ino = 11,
        };
        public int GetFlags() => 0;
    }

    /// <summary>
    /// /dev/urandom — reads return random bytes.
    /// </summary>
    public sealed class DevUrandom : IVirtualFile
    {
        private readonly Random _rng = new Random();

        public int Read(byte[] buffer, int offset, int count)
        {
            var tmp = new byte[count];
            _rng.NextBytes(tmp);
            Array.Copy(tmp, 0, buffer, offset, count);
            return count;
        }
        public int Write(byte[] data, int offset, int count) => count;
        public long Seek(long offset, int whence) => 0;
        public VfsStatResult Stat() => new VfsStatResult
        {
            Mode = 0x2000 | 0x0666,
            Rdev = (1 << 8) | 9,
            Ino = 12,
        };
        public int GetFlags() => 0;
    }

    /// <summary>
    /// Pipe endpoint for inter-process (or intra-process) communication.
    /// </summary>
    public sealed class PipeEndpoint : IVirtualFile
    {
        private readonly Queue<byte> _buffer;
        private readonly bool _isRead;

        public PipeEndpoint(Queue<byte> sharedBuffer, bool isRead)
        {
            _buffer = sharedBuffer;
            _isRead = isRead;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!_isRead) return 0;
            int read = 0;
            lock (_buffer)
            {
                while (read < count && _buffer.Count > 0)
                {
                    buffer[offset + read] = _buffer.Dequeue();
                    read++;
                }
            }
            return read;
        }

        public int Write(byte[] data, int offset, int count)
        {
            if (_isRead) return 0;
            lock (_buffer)
            {
                for (int i = 0; i < count; i++)
                    _buffer.Enqueue(data[offset + i]);
            }
            return count;
        }

        public long Seek(long offset, int whence) => -1;
        public VfsStatResult Stat() => new VfsStatResult
        {
            Mode = 0x1000 | 0x01B0, // S_IFIFO | 0660
            Ino = (ulong)GetHashCode(),
        };
        public int GetFlags() => 0;
    }

    /// <summary>
    /// Virtual file system managing file descriptors and path-to-file mapping.
    /// Provides the file abstraction layer between Linux syscalls and the
    /// UWP sandbox environment. Supports rootfs mounting for running Linux
    /// distribution binaries (bash, coreutils, etc.).
    /// </summary>
    public sealed class VirtualFileSystem
    {
        private readonly Dictionary<int, IVirtualFile> _fds = new Dictionary<int, IVirtualFile>();
        private readonly Dictionary<string, Func<IVirtualFile>> _mountPoints = new Dictionary<string, Func<IVirtualFile>>();
        private int _nextFd = 3;

        // Rootfs manager for full directory hierarchy support
        private RootfsManager? _rootfs;

        // In-memory writable files (for rootfs files that are modified at runtime)
        private readonly Dictionary<string, byte[]> _writableFiles = new Dictionary<string, byte[]>();

        // Tracked directories created at runtime via mkdir
        private readonly HashSet<string> _createdDirs = new HashSet<string>();

        // Mutable /proc/self state for the current emulated process image.
        private string _procSelfExePath = "/usr/bin/program";
        private string _procSelfCmdline = "program\0";
        private string _procSelfComm = "program\n";

        /// <summary>
        /// Initialize the VFS with standard file descriptors and device nodes.
        /// </summary>
        public VirtualFileSystem(
            Func<byte[], int, int, int>? stdinRead,
            Action<byte[], int, int>? stdoutWrite,
            Action<byte[], int, int>? stderrWrite)
        {
            // Standard file descriptors (0=stdin, 1=stdout, 2=stderr)
            _fds[0] = new ConsoleDevice(0, stdinRead, null);
            _fds[1] = new ConsoleDevice(1, null, stdoutWrite);
            _fds[2] = new ConsoleDevice(2, null, stderrWrite ?? stdoutWrite);

            // Device mount points
            _mountPoints["/dev/null"] = () => new DevNull();
            _mountPoints["/dev/zero"] = () => new DevZero();
            _mountPoints["/dev/urandom"] = () => new DevUrandom();
            _mountPoints["/dev/random"] = () => new DevUrandom();
            _mountPoints["/dev/stdin"] = () => _fds[0];
            _mountPoints["/dev/stdout"] = () => _fds[1];
            _mountPoints["/dev/stderr"] = () => _fds[2];
            _mountPoints["/dev/fd/0"] = () => _fds[0];
            _mountPoints["/dev/fd/1"] = () => _fds[1];
            _mountPoints["/dev/fd/2"] = () => _fds[2];
            _mountPoints["/dev/tty"] = () => _fds[0];

            // /proc mount points
            _mountPoints["/proc/self/exe"] = () => new MemoryFile(Encoding.UTF8.GetBytes(_procSelfExePath));
            _mountPoints["/proc/self/maps"] = () => new MemoryFile(Array.Empty<byte>());
            _mountPoints["/proc/self/status"] = () => new MemoryFile(
                Encoding.UTF8.GetBytes("Name:\tprogram\nState:\tR (running)\nPid:\t1000\nUid:\t1000\t1000\t1000\t1000\nGid:\t1000\t1000\t1000\t1000\n"));

            // Additional /proc nodes — from kernel fs/proc/
            _mountPoints["/proc/self/cmdline"] = () => new MemoryFile(
                Encoding.UTF8.GetBytes(_procSelfCmdline));
            _mountPoints["/proc/self/environ"] = () => new MemoryFile(
                Encoding.UTF8.GetBytes("HOME=/\0USER=user\0PATH=/usr/bin:/bin\0TERM=xterm-256color\0"));
            _mountPoints["/proc/self/auxv"] = () => new MemoryFile(Array.Empty<byte>());
            _mountPoints["/proc/self/comm"] = () => new MemoryFile(Encoding.UTF8.GetBytes(_procSelfComm));
            _mountPoints["/proc/self/limits"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                "Limit                     Soft Limit           Hard Limit           Units     \n" +
                "Max open files            1024                 1048576              files     \n" +
                "Max stack size            8388608              unlimited            bytes     \n"));
            _mountPoints["/proc/self/fd/0"] = () => _fds[0];
            _mountPoints["/proc/self/fd/1"] = () => _fds[1];
            _mountPoints["/proc/self/fd/2"] = () => _fds[2];
            _mountPoints["/proc/meminfo"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                "MemTotal:         524288 kB\nMemFree:          262144 kB\nMemAvailable:     393216 kB\nBuffers:           16384 kB\nCached:            65536 kB\n"));
            _mountPoints["/proc/cpuinfo"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                "processor\t: 0\nvendor_id\t: UWPTranslator\nmodel name\t: x86_64 Translated\ncpu MHz\t\t: 1000.000\ncache size\t: 256 KB\n"));
            _mountPoints["/proc/version"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                "Linux version 6.1.0-uwp-translator (uwp@translator) (gcc (UWP) 12.0) #1 SMP\n"));
            _mountPoints["/proc/uptime"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                $"{Environment.TickCount / 1000}.00 {Environment.TickCount / 1000}.00\n"));

            // /etc nodes — common configuration files programs expect
            _mountPoints["/etc/hostname"] = () => new MemoryFile(Encoding.UTF8.GetBytes("localhost\n"));
            _mountPoints["/etc/passwd"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                "root:x:0:0:root:/root:/bin/sh\nuser:x:1000:1000:user:/home/user:/bin/sh\n"));
            _mountPoints["/etc/group"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                "root:x:0:\nuser:x:1000:\n"));
            _mountPoints["/etc/nsswitch.conf"] = () => new MemoryFile(Encoding.UTF8.GetBytes(
                "passwd: files\ngroup: files\nhosts: files dns\n"));
            _mountPoints["/etc/localtime"] = () => new MemoryFile(Array.Empty<byte>());

            // /tmp — writable temp directory support
            _mountPoints["/tmp"] = () => new MemoryFile(Array.Empty<byte>());
        }

        public int Open(string path, int flags, int mode)
        {
            // Check mount points first (device files, proc, etc.)
            if (_mountPoints.TryGetValue(path, out var factory))
            {
                int fd = _nextFd++;
                _fds[fd] = factory();
                return fd;
            }

            // Check writable files (files created/modified at runtime)
            if (_writableFiles.TryGetValue(path, out var wdata))
            {
                int fd = _nextFd++;
                _fds[fd] = new MemoryFile((byte[])wdata.Clone(), flags);
                return fd;
            }

            // Check rootfs manager
            if (_rootfs != null)
            {
                byte[]? rootfsData = _rootfs.ReadFile(path);
                if (rootfsData != null)
                {
                    int fd = _nextFd++;
                    // O_WRONLY=1, O_RDWR=2, O_CREAT=0x40
                    bool writable = (flags & 0x3) != 0;
                    if (writable)
                    {
                        // For writable files, make a copy in writable store
                        byte[] copy = (byte[])rootfsData.Clone();
                        _writableFiles[path] = copy;
                        _fds[fd] = new MemoryFile(copy, flags);
                    }
                    else
                    {
                        _fds[fd] = new MemoryFile((byte[])rootfsData.Clone(), flags);
                    }
                    return fd;
                }
            }

            // O_CREAT flag — create a new writable file
            if ((flags & 0x40) != 0) // O_CREAT
            {
                int fd = _nextFd++;
                byte[] newData = Array.Empty<byte>();
                _writableFiles[path] = newData;
                _fds[fd] = new MemoryFile(newData, flags);
                return fd;
            }

            // Try host filesystem bridge
            int hostResult = TryOpenHostFile(path, flags);
            if (hostResult >= 0)
                return hostResult;

            // For paths that don't exist in our VFS, return ENOENT
            return -Syscall.Errno.ENOENT;
        }

        public int Close(int fd)
        {
            if (fd < 0 || !_fds.ContainsKey(fd))
                return -Syscall.Errno.EBADF;
            if (fd >= 3) // Don't close stdin/stdout/stderr
                _fds.Remove(fd);
            return 0;
        }

        public byte[] Read(int fd, int count)
        {
            if (!_fds.TryGetValue(fd, out var file))
                return Array.Empty<byte>();
            var buffer = new byte[count];
            int bytesRead = file.Read(buffer, 0, count);
            if (bytesRead < count)
                Array.Resize(ref buffer, bytesRead);
            return buffer;
        }

        public int Write(int fd, byte[] data)
        {
            if (!_fds.TryGetValue(fd, out var file))
                return -Syscall.Errno.EBADF;
            return file.Write(data, 0, data.Length);
        }

        public long Lseek(int fd, long offset, int whence)
        {
            if (!_fds.TryGetValue(fd, out var file))
                return -Syscall.Errno.EBADF;
            return file.Seek(offset, whence);
        }

        public VfsStatResult? Fstat(int fd)
        {
            if (!_fds.TryGetValue(fd, out var file))
                return null;
            return file.Stat();
        }

        public VfsStatResult? Stat(string path)
        {
            if (_mountPoints.ContainsKey(path))
            {
                var file = _mountPoints[path]();
                return file.Stat();
            }

            // Check writable files
            if (_writableFiles.TryGetValue(path, out var wdata))
            {
                return new VfsStatResult
                {
                    Mode = 0x8000 | 0x01A4, // S_IFREG | 0644
                    Size = wdata.Length,
                    Ino = (ulong)path.GetHashCode(),
                };
            }

            // Check rootfs manager
            if (_rootfs != null)
            {
                if (_rootfs.IsDirectory(path))
                {
                    return new VfsStatResult
                    {
                        Mode = 0x4000 | 0x01ED, // S_IFDIR | 0755
                        Nlink = 2,
                        Ino = (ulong)path.GetHashCode(),
                    };
                }

                // Check for symlink
                string? linkTarget = _rootfs.ReadLink(path);
                if (linkTarget != null)
                {
                    // For stat, follow symlink and report the target's stat
                    string resolved = _rootfs.ResolveSymlink(path, linkTarget);
                    byte[]? data = _rootfs.ReadFile(resolved);
                    if (data != null)
                    {
                        return new VfsStatResult
                        {
                            Mode = 0x8000 | 0x01ED, // S_IFREG | 0755
                            Size = data.Length,
                            Ino = (ulong)resolved.GetHashCode(),
                        };
                    }
                }

                byte[]? fileData = _rootfs.ReadFile(path);
                if (fileData != null)
                {
                    return new VfsStatResult
                    {
                        Mode = 0x8000 | 0x01ED, // S_IFREG | 0755
                        Size = fileData.Length,
                        Ino = (ulong)path.GetHashCode(),
                    };
                }
            }

            // Built-in directories
            if (IsBuiltinDirectory(path))
            {
                return new VfsStatResult
                {
                    Mode = 0x4000 | 0x01ED, // S_IFDIR | 0755
                    Nlink = 2,
                    Ino = (ulong)path.GetHashCode(),
                };
            }

            // Check created directories
            if (_createdDirs.Contains(path))
            {
                return new VfsStatResult
                {
                    Mode = 0x4000 | 0x01ED, // S_IFDIR | 0755
                    Nlink = 2,
                    Ino = (ulong)path.GetHashCode(),
                };
            }

            return null;
        }

        public bool Access(string path, int mode)
        {
            return _mountPoints.ContainsKey(path) ||
                   _writableFiles.ContainsKey(path) ||
                   IsBuiltinDirectory(path) ||
                   _createdDirs.Contains(path) ||
                   (_rootfs != null && _rootfs.Exists(path));
        }

        private static bool IsBuiltinDirectory(string path)
        {
            return path == "/" || path == "/dev" || path == "/proc" || path == "/tmp" ||
                   path == "/etc" || path == "/proc/self" || path == "/proc/self/fd" ||
                   path == "/dev/fd" || path == "/home" || path == "/home/user" ||
                   path == "/usr" || path == "/usr/bin" || path == "/bin" ||
                   path == "/sbin" || path == "/usr/sbin" || path == "/lib" ||
                   path == "/lib64" || path == "/usr/lib" || path == "/var" ||
                   path == "/root" || path == "/run" || path == "/sys" ||
                   path == "/opt" || path == "/usr/local" || path == "/usr/local/bin";
        }

        public int Dup(int oldfd)
        {
            if (!_fds.TryGetValue(oldfd, out var file))
                return -Syscall.Errno.EBADF;
            int newfd = _nextFd++;
            _fds[newfd] = file;
            return newfd;
        }

        public int Dup2(int oldfd, int newfd)
        {
            if (!_fds.TryGetValue(oldfd, out var file))
                return -Syscall.Errno.EBADF;
            _fds[newfd] = file;
            return newfd;
        }

        public int Pipe(out int readFd, out int writeFd)
        {
            var buffer = new Queue<byte>();
            readFd = _nextFd++;
            writeFd = _nextFd++;
            _fds[readFd] = new PipeEndpoint(buffer, true);
            _fds[writeFd] = new PipeEndpoint(buffer, false);
            return 0;
        }

        public int GetFlags(int fd)
        {
            if (!_fds.TryGetValue(fd, out var file))
                return -Syscall.Errno.EBADF;
            return file.GetFlags();
        }

        /// <summary>
        /// Register a file from an external source (e.g., UWP file picker).
        /// </summary>
        public int RegisterFile(string path, byte[] data)
        {
            _mountPoints[path] = () => new MemoryFile((byte[])data.Clone());
            return 0;
        }

        /// <summary>
        /// Register a virtual file system node accessible at the given path.
        /// </summary>
        public void Mount(string path, Func<IVirtualFile> fileFactory)
        {
            _mountPoints[path] = fileFactory;
        }

        /// <summary>
        /// Update the process metadata exposed through /proc/self.
        /// </summary>
        public void SetProcessInfo(string exePath, IReadOnlyList<string>? argv = null)
        {
            _procSelfExePath = string.IsNullOrWhiteSpace(exePath) ? "/usr/bin/program" : exePath;

            if (argv != null && argv.Count > 0)
            {
                _procSelfCmdline = string.Join("\0", argv) + "\0";
                _procSelfComm = argv[0] + "\n";
            }
            else
            {
                _procSelfCmdline = _procSelfExePath + "\0";
                _procSelfComm = _procSelfExePath + "\n";
            }
        }

        /// <summary>
        /// Set the rootfs manager for full directory hierarchy support.
        /// </summary>
        public void SetRootfsManager(RootfsManager rootfs)
        {
            _rootfs = rootfs;
        }

        /// <summary>
        /// Get the rootfs manager (if set).
        /// </summary>
        public RootfsManager? GetRootfs() => _rootfs;

        /// <summary>
        /// Create a directory in the virtual filesystem.
        /// </summary>
        public int MakeDirectory(string path)
        {
            path = path.TrimEnd('/');
            if (string.IsNullOrEmpty(path)) return -Syscall.Errno.EINVAL;
            if (_createdDirs.Contains(path) || IsBuiltinDirectory(path) ||
                (_rootfs != null && _rootfs.IsDirectory(path)))
                return -Syscall.Errno.EEXIST;

            _createdDirs.Add(path);
            return 0;
        }

        /// <summary>
        /// Remove a file from the virtual filesystem.
        /// </summary>
        public int Unlink(string path)
        {
            if (_writableFiles.Remove(path))
                return 0;
            if (_mountPoints.Remove(path))
                return 0;
            // Can't remove rootfs files
            return -Syscall.Errno.EROFS;
        }

        /// <summary>
        /// Read symlink target from rootfs.
        /// </summary>
        public string? ReadSymlink(string path)
        {
            if (path == "/proc/self/exe")
                return _procSelfExePath;

            return _rootfs?.ReadLink(path);
        }

        /// <summary>
        /// List entries in a directory (for getdents64).
        /// Returns entry names, or null if path is not a directory.
        /// </summary>
        public List<string>? ListDirectory(string path)
        {
            // Check if it's a directory
            if (!IsBuiltinDirectory(path) && !_createdDirs.Contains(path) &&
                (_rootfs == null || !_rootfs.IsDirectory(path)))
            {
                return null;
            }

            var entries = new List<string> { ".", ".." };

            // From rootfs
            if (_rootfs != null)
            {
                entries.AddRange(_rootfs.ListDirectory(path));
            }

            // From mount points
            string prefix = path.EndsWith("/") ? path : path + "/";
            var seen = new HashSet<string>(entries);
            foreach (var mp in _mountPoints.Keys)
            {
                if (mp.StartsWith(prefix) && mp.Length > prefix.Length)
                {
                    string relative = mp.Substring(prefix.Length);
                    int slashIdx = relative.IndexOf('/');
                    string entry = slashIdx >= 0 ? relative.Substring(0, slashIdx) : relative;
                    if (seen.Add(entry))
                        entries.Add(entry);
                }
            }

            // From writable files
            foreach (var wf in _writableFiles.Keys)
            {
                if (wf.StartsWith(prefix) && wf.Length > prefix.Length)
                {
                    string relative = wf.Substring(prefix.Length);
                    int slashIdx = relative.IndexOf('/');
                    string entry = slashIdx >= 0 ? relative.Substring(0, slashIdx) : relative;
                    if (seen.Add(entry))
                        entries.Add(entry);
                }
            }

            return entries;
        }

        /// <summary>
        /// Read a file's raw bytes from the VFS/rootfs (used by execve).
        /// </summary>
        public byte[]? ReadFileBytes(string path)
        {
            // Check writable files first
            if (_writableFiles.TryGetValue(path, out var wdata))
                return wdata;

            // Check rootfs
            if (_rootfs != null)
            {
                byte[]? data = _rootfs.ReadFile(path);
                if (data != null)
                    return data;
            }

            return null;
        }

        /// <summary>
        /// Register a host directory as accessible from the virtual filesystem.
        /// This bridges UWP app local storage into the emulated Linux environment,
        /// allowing binaries to read/write files from Xbox One local storage.
        /// </summary>
        public void MountHostDirectory(string vfsPath, Func<string, byte[]?> readFile, Func<string, byte[], bool> writeFile, Func<string, bool> fileExists)
        {
            _hostDirectories[vfsPath] = new HostDirectoryBridge
            {
                ReadFile = readFile,
                WriteFile = writeFile,
                FileExists = fileExists,
            };
        }

        // Host filesystem bridge for UWP local storage integration
        private readonly Dictionary<string, HostDirectoryBridge> _hostDirectories = new Dictionary<string, HostDirectoryBridge>();

        private class HostDirectoryBridge
        {
            public Func<string, byte[]?> ReadFile { get; set; } = _ => null;
            public Func<string, byte[], bool> WriteFile { get; set; } = (_, __) => false;
            public Func<string, bool> FileExists { get; set; } = _ => false;
        }

        /// <summary>
        /// Try to open a file via the host filesystem bridge.
        /// </summary>
        private int TryOpenHostFile(string path, int flags)
        {
            foreach (var kvp in _hostDirectories)
            {
                if (path.StartsWith(kvp.Key))
                {
                    string relative = path.Substring(kvp.Key.Length).TrimStart('/');
                    if (kvp.Value.FileExists(relative))
                    {
                        byte[]? data = kvp.Value.ReadFile(relative);
                        if (data != null)
                        {
                            int fd = _nextFd++;
                            _fds[fd] = new MemoryFile(data);
                            return fd;
                        }
                    }
                }
            }
            return -Syscall.Errno.ENOENT;
        }
    }
}
