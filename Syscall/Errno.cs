// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Linux error number constants.
// Derived from the Linux kernel source:
//   include/uapi/asm-generic/errno-base.h
//   include/uapi/asm-generic/errno.h

namespace LinuxBinaryTranslator.Syscall
{
    /// <summary>
    /// Linux errno values as defined in the kernel headers.
    /// These are returned as negative values from syscalls on error.
    /// </summary>
    public static class Errno
    {
        // errno-base.h (1-34)
        public const int EPERM = 1;
        public const int ENOENT = 2;
        public const int ESRCH = 3;
        public const int EINTR = 4;
        public const int EIO = 5;
        public const int ENXIO = 6;
        public const int E2BIG = 7;
        public const int ENOEXEC = 8;
        public const int EBADF = 9;
        public const int ECHILD = 10;
        public const int EAGAIN = 11;
        public const int ENOMEM = 12;
        public const int EACCES = 13;
        public const int EFAULT = 14;
        public const int ENOTBLK = 15;
        public const int EBUSY = 16;
        public const int EEXIST = 17;
        public const int EXDEV = 18;
        public const int ENODEV = 19;
        public const int ENOTDIR = 20;
        public const int EISDIR = 21;
        public const int EINVAL = 22;
        public const int ENFILE = 23;
        public const int EMFILE = 24;
        public const int ENOTTY = 25;
        public const int ETXTBSY = 26;
        public const int EFBIG = 27;
        public const int ENOSPC = 28;
        public const int ESPIPE = 29;
        public const int EROFS = 30;
        public const int EMLINK = 31;
        public const int EPIPE = 32;
        public const int EDOM = 33;
        public const int ERANGE = 34;

        // errno.h (35+)
        public const int EDEADLK = 35;
        public const int ENAMETOOLONG = 36;
        public const int ENOLCK = 37;
        public const int ENOSYS = 38;
        public const int ENOTEMPTY = 39;
        public const int ELOOP = 40;
        public const int EWOULDBLOCK = EAGAIN;
        public const int ENOMSG = 42;
        public const int EIDRM = 43;
        public const int ECHRNG = 44;
        public const int EL2NSYNC = 45;
        public const int EL3HLT = 46;
        public const int EL3RST = 47;
        public const int ELNRNG = 48;
        public const int EUNATCH = 49;
        public const int ENOCSI = 50;
        public const int EL2HLT = 51;
        public const int EBADE = 52;
        public const int EBADR = 53;
        public const int EXFULL = 54;
        public const int ENOANO = 55;
        public const int EBADRQC = 56;
        public const int EBADSLT = 57;
        public const int EDEADLOCK = EDEADLK;
        public const int EBFONT = 59;
        public const int ENOSTR = 60;
        public const int ENODATA = 61;
        public const int ETIME = 62;
        public const int ENOSR = 63;
        public const int ENONET = 64;
        public const int ENOPKG = 65;
        public const int EREMOTE = 66;
        public const int ENOLINK = 67;
        public const int EADV = 68;
        public const int ESRMNT = 69;
        public const int ECOMM = 70;
        public const int EPROTO = 71;
        public const int EMULTIHOP = 72;
        public const int EDOTDOT = 73;
        public const int EBADMSG = 74;
        public const int EOVERFLOW = 75;
        public const int ENOTUNIQ = 76;
        public const int EBADFD = 77;
        public const int EREMCHG = 78;
        public const int ELIBACC = 79;
        public const int ELIBBAD = 80;
        public const int ELIBSCN = 81;
        public const int ELIBMAX = 82;
        public const int ELIBEXEC = 83;
        public const int EILSEQ = 84;
        public const int ERESTART = 85;
        public const int ESTRPIPE = 86;
        public const int EUSERS = 87;
        public const int ENOTSOCK = 88;
        public const int EDESTADDRREQ = 89;
        public const int EMSGSIZE = 90;
        public const int EPROTOTYPE = 91;
        public const int ENOPROTOOPT = 92;
        public const int EPROTONOSUPPORT = 93;
        public const int ESOCKTNOSUPPORT = 94;
        public const int EOPNOTSUPP = 95;
        public const int EPFNOSUPPORT = 96;
        public const int EAFNOSUPPORT = 97;
        public const int EADDRINUSE = 98;
        public const int EADDRNOTAVAIL = 99;
        public const int ENETDOWN = 100;
        public const int ENETUNREACH = 101;
        public const int ENETRESET = 102;
        public const int ECONNABORTED = 103;
        public const int ECONNRESET = 104;
        public const int ENOBUFS = 105;
        public const int EISCONN = 106;
        public const int ENOTCONN = 107;
        public const int ESHUTDOWN = 108;
        public const int ETOOMANYREFS = 109;
        public const int ETIMEDOUT = 110;
        public const int ECONNREFUSED = 111;
        public const int EHOSTDOWN = 112;
        public const int EHOSTUNREACH = 113;
        public const int EALREADY = 114;
        public const int EINPROGRESS = 115;
        public const int ESTALE = 116;
        public const int EUCLEAN = 117;
        public const int ENOTNAM = 118;
        public const int ENAVAIL = 119;
        public const int EISNAM = 120;
        public const int EREMOTEIO = 121;
        public const int EDQUOT = 122;
        public const int ENOMEDIUM = 123;
        public const int EMEDIUMTYPE = 124;
        public const int ECANCELED = 125;
        public const int ENOKEY = 126;
        public const int EKEYEXPIRED = 127;
        public const int EKEYREVOKED = 128;
        public const int EKEYREJECTED = 129;
        public const int EOWNERDEAD = 130;
        public const int ENOTRECOVERABLE = 131;
        public const int ERFKILL = 132;
        public const int EHWPOISON = 133;
    }
}
