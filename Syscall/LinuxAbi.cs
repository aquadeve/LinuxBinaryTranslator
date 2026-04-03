// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Linux ABI constants for file operations, memory mapping, etc.
// Derived from the Linux kernel source:
//   include/uapi/asm-generic/fcntl.h
//   include/uapi/asm-generic/mman-common.h
//   arch/x86/include/uapi/asm/mman.h

namespace LinuxBinaryTranslator.Syscall
{
    /// <summary>
    /// Linux open/fcntl flags from kernel fcntl.h.
    /// </summary>
    public static class OpenFlags
    {
        public const int O_RDONLY = 0x0000;
        public const int O_WRONLY = 0x0001;
        public const int O_RDWR = 0x0002;
        public const int O_ACCMODE = 0x0003;
        public const int O_CREAT = 0x0040;     // 0100 octal
        public const int O_EXCL = 0x0080;      // 0200
        public const int O_NOCTTY = 0x0100;    // 0400
        public const int O_TRUNC = 0x0200;     // 01000
        public const int O_APPEND = 0x0400;    // 02000
        public const int O_NONBLOCK = 0x0800;  // 04000
        public const int O_DSYNC = 0x1000;     // 010000
        public const int O_DIRECT = 0x4000;    // 040000
        public const int O_LARGEFILE = 0x8000; // 0100000
        public const int O_DIRECTORY = 0x10000; // 0200000
        public const int O_NOFOLLOW = 0x20000; // 0400000
        public const int O_NOATIME = 0x40000;  // 01000000
        public const int O_CLOEXEC = 0x80000;  // 02000000
        public const int O_SYNC = 0x101000;    // __O_SYNC | O_DSYNC
        public const int O_PATH = 0x200000;    // 010000000
        public const int O_TMPFILE = 0x410000; // __O_TMPFILE | O_DIRECTORY

        public const int AT_FDCWD = -100;
    }

    /// <summary>
    /// Linux fcntl command constants from kernel fcntl.h.
    /// </summary>
    public static class FcntlCmd
    {
        public const int F_DUPFD = 0;
        public const int F_GETFD = 1;
        public const int F_SETFD = 2;
        public const int F_GETFL = 3;
        public const int F_SETFL = 4;
        public const int F_GETLK = 5;
        public const int F_SETLK = 6;
        public const int F_SETLKW = 7;
        public const int F_SETOWN = 8;
        public const int F_GETOWN = 9;
        public const int F_SETSIG = 10;
        public const int F_GETSIG = 11;
        public const int F_DUPFD_CLOEXEC = 1030;

        public const int FD_CLOEXEC = 1;
    }

    /// <summary>
    /// Linux memory mapping constants from kernel mman-common.h.
    /// </summary>
    public static class MmapFlags
    {
        // Protection flags
        public const int PROT_NONE = 0x0;
        public const int PROT_READ = 0x1;
        public const int PROT_WRITE = 0x2;
        public const int PROT_EXEC = 0x4;

        // Mapping type flags
        public const int MAP_SHARED = 0x01;
        public const int MAP_PRIVATE = 0x02;
        public const int MAP_SHARED_VALIDATE = 0x03;
        public const int MAP_TYPE = 0x0F;

        // Mapping flags
        public const int MAP_FIXED = 0x10;
        public const int MAP_ANONYMOUS = 0x20;
        public const int MAP_GROWSDOWN = 0x0100;
        public const int MAP_DENYWRITE = 0x0800;
        public const int MAP_EXECUTABLE = 0x1000;
        public const int MAP_LOCKED = 0x2000;
        public const int MAP_NORESERVE = 0x4000;
        public const int MAP_POPULATE = 0x8000;
        public const int MAP_NONBLOCK = 0x10000;
        public const int MAP_STACK = 0x20000;
        public const int MAP_HUGETLB = 0x40000;
        public const int MAP_SYNC = 0x80000;
        public const int MAP_FIXED_NOREPLACE = 0x100000;

        // Failed map return
        public const long MAP_FAILED = -1;
    }

    /// <summary>
    /// Linux file mode/stat constants from kernel stat.h.
    /// </summary>
    public static class FileMode
    {
        public const int S_IFMT = 0xF000;
        public const int S_IFSOCK = 0xC000;
        public const int S_IFLNK = 0xA000;
        public const int S_IFREG = 0x8000;
        public const int S_IFBLK = 0x6000;
        public const int S_IFDIR = 0x4000;
        public const int S_IFCHR = 0x2000;
        public const int S_IFIFO = 0x1000;

        public const int S_ISUID = 0x0800;
        public const int S_ISGID = 0x0400;
        public const int S_ISVTX = 0x0200;

        public const int S_IRWXU = 0x01C0;
        public const int S_IRUSR = 0x0100;
        public const int S_IWUSR = 0x0080;
        public const int S_IXUSR = 0x0040;

        public const int S_IRWXG = 0x0038;
        public const int S_IRGRP = 0x0020;
        public const int S_IWGRP = 0x0010;
        public const int S_IXGRP = 0x0008;

        public const int S_IRWXO = 0x0007;
        public const int S_IROTH = 0x0004;
        public const int S_IWOTH = 0x0002;
        public const int S_IXOTH = 0x0001;
    }

    /// <summary>
    /// Linux seek whence constants.
    /// </summary>
    public static class SeekWhence
    {
        public const int SEEK_SET = 0;
        public const int SEEK_CUR = 1;
        public const int SEEK_END = 2;
    }

    /// <summary>
    /// Linux ioctl/termios constants from kernel termbits.h.
    /// </summary>
    public static class TermiosConstants
    {
        // ioctl commands
        public const uint TCGETS = 0x5401;
        public const uint TCSETS = 0x5402;
        public const uint TCSETSW = 0x5403;
        public const uint TCSETSF = 0x5404;
        public const uint TIOCGWINSZ = 0x5413;
        public const uint TIOCSWINSZ = 0x5414;
        public const uint TIOCGPGRP = 0x540F;
        public const uint TIOCSPGRP = 0x5410;

        // c_lflag bits
        public const uint ISIG = 0x0001;
        public const uint ICANON = 0x0002;
        public const uint ECHO = 0x0008;
        public const uint ECHOE = 0x0010;
        public const uint ECHOK = 0x0020;
        public const uint ECHONL = 0x0040;
        public const uint IEXTEN = 0x8000;

        // c_iflag bits
        public const uint IGNBRK = 0x0001;
        public const uint BRKINT = 0x0002;
        public const uint IGNPAR = 0x0004;
        public const uint ICRNL = 0x0100;
        public const uint IXON = 0x0400;
        public const uint IXOFF = 0x1000;

        // c_oflag bits
        public const uint OPOST = 0x0001;
        public const uint ONLCR = 0x0004;

        // NCCS
        public const int NCCS = 19;
    }
}
