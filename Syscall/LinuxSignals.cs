// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Linux signal constants.
// Derived from the Linux kernel source:
//   include/uapi/asm-generic/signal.h
//   arch/x86/include/uapi/asm/signal.h

namespace LinuxBinaryTranslator.Syscall
{
    /// <summary>
    /// Linux signal numbers and related constants from the kernel.
    /// </summary>
    public static class Signals
    {
        public const int SIGHUP = 1;
        public const int SIGINT = 2;
        public const int SIGQUIT = 3;
        public const int SIGILL = 4;
        public const int SIGTRAP = 5;
        public const int SIGABRT = 6;
        public const int SIGIOT = 6;
        public const int SIGBUS = 7;
        public const int SIGFPE = 8;
        public const int SIGKILL = 9;
        public const int SIGUSR1 = 10;
        public const int SIGSEGV = 11;
        public const int SIGUSR2 = 12;
        public const int SIGPIPE = 13;
        public const int SIGALRM = 14;
        public const int SIGTERM = 15;
        public const int SIGSTKFLT = 16;
        public const int SIGCHLD = 17;
        public const int SIGCONT = 18;
        public const int SIGSTOP = 19;
        public const int SIGTSTP = 20;
        public const int SIGTTIN = 21;
        public const int SIGTTOU = 22;
        public const int SIGURG = 23;
        public const int SIGXCPU = 24;
        public const int SIGXFSZ = 25;
        public const int SIGVTALRM = 26;
        public const int SIGPROF = 27;
        public const int SIGWINCH = 28;
        public const int SIGIO = 29;
        public const int SIGPOLL = SIGIO;
        public const int SIGPWR = 30;
        public const int SIGSYS = 31;
        public const int SIGRTMIN = 32;
        public const int SIGRTMAX = 64;

        // Signal action constants
        public const long SIG_DFL = 0;
        public const long SIG_IGN = 1;
        public const long SIG_ERR = -1;

        // sa_flags from kernel signal.h
        public const int SA_NOCLDSTOP = 0x00000001;
        public const int SA_NOCLDWAIT = 0x00000002;
        public const int SA_SIGINFO = 0x00000004;
        public const int SA_ONSTACK = 0x08000000;
        public const int SA_RESTART = 0x10000000;
        public const int SA_NODEFER = 0x40000000;
        public const uint SA_RESETHAND = 0x80000000;
        public const int SA_RESTORER = 0x04000000;

        // sigprocmask how constants
        public const int SIG_BLOCK = 0;
        public const int SIG_UNBLOCK = 1;
        public const int SIG_SETMASK = 2;

        // Signal stack constants
        public const int MINSIGSTKSZ = 2048;
        public const int SIGSTKSZ = 8192;
    }
}
