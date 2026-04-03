# Linux Binary Translator for UWP / Xbox One

A C# UWP application that runs static Linux x86_64 ELF CLI binaries on Windows
and Xbox One by natively translating x86_64 instructions into C# delegates at
the basic-block level.

## Architecture

This is a **fresh implementation** (not a port of FLinux) that uses the Linux
kernel source tree in the repository root as a reference for correct ABI
constants, syscall tables, ELF structures, and signal definitions.

### Core components

| Component | Path | Description |
|-----------|------|-------------|
| **ELF Loader** | `Elf/` | Parses and loads 64-bit ELF binaries per kernel `include/uapi/linux/elf.h` |
| **Block Translator** | `Cpu/Translation/` | Translates x86_64 basic blocks into cached C# delegates for native execution |
| **CPU State** | `Cpu/CpuState.cs` | Full x86_64 register file (16 GPRs, RFLAGS, FS/GS base, segments) |
| **Instruction Decoder** | `Cpu/InstructionDecoder.cs` | Decodes x86_64 machine code with REX prefixes, ModRM, SIB |
| **Memory Manager** | `Memory/` | Linux-style virtual memory (mmap/munmap/brk/mprotect, page-aligned) |
| **Syscall Handler** | `Syscall/` | 50+ Linux syscalls from `arch/x86/entry/syscalls/syscall_64.tbl` |
| **Virtual File System** | `FileSystem/` | VFS with /dev, /proc, /etc nodes and host filesystem bridge |
| **Terminal Emulator** | `Terminal/` | ANSI terminal with Xbox gamepad input mapping |
| **UWP Shell** | `MainPage.xaml` | Dark-themed terminal UI with gamepad support |

### Native Translation Approach

Instead of interpreting instructions one at a time, the translator:

1. **Decodes** a basic block of x86_64 instructions until a terminator (branch, call, ret, syscall)
2. **Translates** the entire block into a single C# delegate (`TranslatedBlock`)
3. **Caches** the delegate so subsequent visits execute managed code directly
4. **Chains** blocks together at runtime — the execution engine runs block after block

This approach eliminates per-instruction decode overhead and allows the .NET
JIT/AOT compiler to optimize translated blocks as native code.

### Supported x86_64 Instructions

- **Data movement**: MOV, MOVZX, MOVSX, MOVSXD, LEA, PUSH, POP, XCHG, CMOVcc
- **Arithmetic**: ADD, SUB, ADC, SBB, INC, DEC, NEG, MUL, IMUL, DIV, IDIV
- **Logic**: AND, OR, XOR, NOT, TEST
- **Shifts**: SHL, SHR, SAR, ROL, ROR, RCL, RCR
- **String ops**: MOVS, STOS, LODS, CMPS, SCAS (with REP prefix support)
- **Bit ops**: BT, BTS, BTR, BTC, BSF, BSR, POPCNT, BSWAP
- **Atomic**: CMPXCHG, XADD
- **Control flow**: JMP, CALL, RET, Jcc, SETcc, LOOP
- **System**: SYSCALL, INT 0x80, CPUID, RDTSC
- **Misc**: NOP, CLD, STD, CLC, STC, LEAVE, ENTER, CBW/CWDE/CDQE, CWD/CDQ/CQO

### Linux Syscalls

Over 50 syscalls implemented from the kernel source `arch/x86/entry/syscalls/syscall_64.tbl`:

- **File I/O**: read, write, open, close, lseek, stat, fstat, openat, readv, writev, pread64, pwrite64
- **Memory**: mmap, mprotect, munmap, brk, madvise
- **Process**: getpid, getppid, gettid, exit, exit_group
- **User/Group**: getuid, geteuid, getgid, getegid
- **Terminal**: ioctl (TCGETS, TIOCGWINSZ, TCSETS, etc.)
- **Time**: gettimeofday, clock_gettime, clock_getres, nanosleep, clock_nanosleep
- **Filesystem**: getcwd, chdir, access, readlink, getdents64, fcntl
- **Signals**: rt_sigaction, rt_sigprocmask (handler registration)
- **Sync**: futex (single-threaded WAIT/WAKE), poll
- **Info**: uname, sysinfo, getrlimit, prlimit64, getrandom, getrusage, times, arch_prctl

### Virtual File System

The VFS provides Linux-compatible device nodes and pseudo-filesystems:

- `/dev/null`, `/dev/zero`, `/dev/urandom`, `/dev/random`, `/dev/tty`
- `/dev/stdin`, `/dev/stdout`, `/dev/stderr` (console bridging)
- `/proc/self/exe`, `/proc/self/maps`, `/proc/self/status`, `/proc/self/cmdline`
- `/proc/self/environ`, `/proc/self/comm`, `/proc/self/limits`, `/proc/self/fd/`
- `/proc/cpuinfo`, `/proc/meminfo`, `/proc/version`, `/proc/uptime`
- `/etc/hostname`, `/etc/passwd`, `/etc/group`, `/etc/nsswitch.conf`
- **Host filesystem bridge** — mount UWP local storage directories into the VFS

## Xbox One Compatibility

The project is designed for Xbox One deployment:

- **UWP-only APIs**: No Win32 dependencies; runs in the UWP app container
- **.NET Native AOT**: Release builds use the .NET Native toolchain for Xbox
- **Gamepad controls**:
  - **A** → Submit input / Enter
  - **B** → Ctrl+C (interrupt)
  - **X** → Load ELF binary (file picker)
  - **Y** → Clear terminal
  - **DPad** → Arrow keys
  - **LB** → Tab, **RB** → Backspace
- **Memory-constrained**: Default 512 MB virtual memory limit (configurable)
- **60fps UI**: Rate-limited terminal output updates

## Building

Requirements:
- Visual Studio 2019+ with UWP workload
- Windows SDK 10.0.19041+
- .NET Universal Windows Platform 6.2.14+

```
cd LinuxBinaryTranslator
dotnet restore
msbuild /p:Configuration=Release /p:Platform=x64
```

For Xbox One deployment, build with:
```
msbuild /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Always
```

## Running Linux Binaries

1. Build a static Linux x86_64 binary (e.g., `musl-gcc -static hello.c -o hello`)
2. Launch the UWP app
3. Press **X** (Xbox) or click **Load Binary** to select the ELF file
4. The binary runs in the terminal emulator

## Limitations

- **Static and dynamic binaries** — PT_INTERP interpreter (ld-linux.so) loaded from rootfs; full dynamic linking depends on the rootfs having correct shared libraries
- **Single-threaded** — clone/fork return ENOSYS; futex supports single-threaded patterns
- **SSE/SSE2/SSE3/SSE4 arithmetic** — scalar and packed float32/float64 operations implemented; x87 FPU and AVX are no-op stubs
- **No networking** — socket syscalls return ENOSYS
- **Writable virtual filesystem** — mkdir/unlink/creat work on VFS; rootfs files are read-only unless modified via O_CREAT/O_WRONLY at runtime

## License

GPL v3+ — see COPYING in the repository root.

## Kernel Reference

ABI constants, syscall tables, and struct layouts are sourced from the Linux
kernel source in the repository root:

- `arch/x86/entry/syscalls/syscall_64.tbl` — syscall numbers
- `include/uapi/linux/elf.h` — ELF format definitions
- `include/uapi/asm-generic/errno-base.h` — error codes
- `include/uapi/asm-generic/signal.h` — signal numbers
- `arch/x86/include/uapi/asm/prctl.h` — arch_prctl constants
- `include/uapi/linux/stat.h` — stat structure layout
- `include/uapi/asm-generic/fcntl.h` — file control flags
