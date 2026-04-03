// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// x86_64 CPU register state for the translated process.
// Models the register file as defined by the AMD64 ABI and
// the Linux kernel's pt_regs structure (arch/x86/include/asm/ptrace.h).

using System;
using System.Runtime.CompilerServices;

namespace LinuxBinaryTranslator.Cpu
{
    /// <summary>
    /// x86_64 CPU flags register bits.
    /// </summary>
    [Flags]
    public enum X86Flags : ulong
    {
        CF = 1UL << 0,   // Carry
        PF = 1UL << 2,   // Parity
        AF = 1UL << 4,   // Auxiliary carry
        ZF = 1UL << 6,   // Zero
        SF = 1UL << 7,   // Sign
        TF = 1UL << 8,   // Trap
        IF = 1UL << 9,   // Interrupt enable
        DF = 1UL << 10,  // Direction
        OF = 1UL << 11,  // Overflow
        IOPL = 3UL << 12,
        NT = 1UL << 14,  // Nested task
        RF = 1UL << 16,  // Resume
        VM = 1UL << 17,  // Virtual 8086 mode
        AC = 1UL << 18,  // Alignment check
        VIF = 1UL << 19, // Virtual interrupt
        VIP = 1UL << 20, // Virtual interrupt pending
        ID = 1UL << 21,  // CPUID detection
    }

    /// <summary>
    /// x86_64 register file. Mirrors the kernel's pt_regs layout but stored
    /// as individual fields for fast access during translation.
    /// </summary>
    public sealed class CpuState
    {
        // General purpose registers (AMD64)
        public ulong RAX;
        public ulong RBX;
        public ulong RCX;
        public ulong RDX;
        public ulong RSI;
        public ulong RDI;
        public ulong RBP;
        public ulong RSP;
        public ulong R8;
        public ulong R9;
        public ulong R10;
        public ulong R11;
        public ulong R12;
        public ulong R13;
        public ulong R14;
        public ulong R15;

        // Instruction pointer
        public ulong RIP;

        // Flags register
        public ulong RFLAGS;

        // Segment registers (rarely used in 64-bit mode)
        public ushort CS;
        public ushort DS;
        public ushort ES;
        public ushort FS;
        public ushort GS;
        public ushort SS;

        // FS/GS base (used for TLS)
        public ulong FSBase;
        public ulong GSBase;

        // SSE/SSE2 XMM registers (128-bit, stored as two 64-bit halves)
        // XMM0-XMM15 are required by the AMD64 ABI for floating-point
        // and are commonly used even in simple static binaries.
        // We store them as ulong pairs: [low, high] per register.
        public ulong[] XmmLow = new ulong[16];
        public ulong[] XmmHigh = new ulong[16];

        // MXCSR — SSE control/status register (default value per AMD64 ABI)
        public uint MXCSR = 0x1F80; // Default: all exceptions masked, round-to-nearest

        /// <summary>
        /// Indicates that the process has called exit/exit_group.
        /// </summary>
        public bool Halted;

        /// <summary>
        /// Exit code set by exit/exit_group syscall.
        /// </summary>
        public int ExitCode;

        // 32-bit register accessors (lower 32 bits, zero-extends on write per AMD64 spec)
        public uint EAX { get => (uint)RAX; set => RAX = value; }
        public uint EBX { get => (uint)RBX; set => RBX = value; }
        public uint ECX { get => (uint)RCX; set => RCX = value; }
        public uint EDX { get => (uint)RDX; set => RDX = value; }
        public uint ESI { get => (uint)RSI; set => RSI = value; }
        public uint EDI { get => (uint)RDI; set => RDI = value; }
        public uint EBP { get => (uint)RBP; set => RBP = value; }
        public uint ESP { get => (uint)RSP; set => RSP = value; }
        public uint R8D { get => (uint)R8; set => R8 = value; }
        public uint R9D { get => (uint)R9; set => R9 = value; }
        public uint R10D { get => (uint)R10; set => R10 = value; }
        public uint R11D { get => (uint)R11; set => R11 = value; }
        public uint R12D { get => (uint)R12; set => R12 = value; }
        public uint R13D { get => (uint)R13; set => R13 = value; }
        public uint R14D { get => (uint)R14; set => R14 = value; }
        public uint R15D { get => (uint)R15; set => R15 = value; }

        // 16-bit register accessors (lower 16 bits)
        public ushort AX { get => (ushort)RAX; set => RAX = (RAX & 0xFFFFFFFFFFFF0000UL) | value; }
        public ushort BX { get => (ushort)RBX; set => RBX = (RBX & 0xFFFFFFFFFFFF0000UL) | value; }
        public ushort CX { get => (ushort)RCX; set => RCX = (RCX & 0xFFFFFFFFFFFF0000UL) | value; }
        public ushort DX { get => (ushort)RDX; set => RDX = (RDX & 0xFFFFFFFFFFFF0000UL) | value; }

        // 8-bit register accessors
        public byte AL { get => (byte)RAX; set => RAX = (RAX & 0xFFFFFFFFFFFFFF00UL) | value; }
        public byte AH { get => (byte)(RAX >> 8); set => RAX = (RAX & 0xFFFFFFFFFFFF00FFUL) | ((ulong)value << 8); }
        public byte BL { get => (byte)RBX; set => RBX = (RBX & 0xFFFFFFFFFFFFFF00UL) | value; }
        public byte BH { get => (byte)(RBX >> 8); set => RBX = (RBX & 0xFFFFFFFFFFFF00FFUL) | ((ulong)value << 8); }
        public byte CL { get => (byte)RCX; set => RCX = (RCX & 0xFFFFFFFFFFFFFF00UL) | value; }
        public byte CH { get => (byte)(RCX >> 8); set => RCX = (RCX & 0xFFFFFFFFFFFF00FFUL) | ((ulong)value << 8); }
        public byte DL { get => (byte)RDX; set => RDX = (RDX & 0xFFFFFFFFFFFFFF00UL) | value; }
        public byte DH { get => (byte)(RDX >> 8); set => RDX = (RDX & 0xFFFFFFFFFFFF00FFUL) | ((ulong)value << 8); }
        public byte SIL { get => (byte)RSI; set => RSI = (RSI & 0xFFFFFFFFFFFFFF00UL) | value; }
        public byte DIL { get => (byte)RDI; set => RDI = (RDI & 0xFFFFFFFFFFFFFF00UL) | value; }
        public byte BPL { get => (byte)RBP; set => RBP = (RBP & 0xFFFFFFFFFFFFFF00UL) | value; }
        public byte SPL { get => (byte)RSP; set => RSP = (RSP & 0xFFFFFFFFFFFFFF00UL) | value; }

        /// <summary>
        /// Access a general-purpose register by index (0=RAX, 1=RCX, 2=RDX, 3=RBX,
        /// 4=RSP, 5=RBP, 6=RSI, 7=RDI, 8-15=R8-R15).
        /// Follows the ModRM/SIB encoding order.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong GetGpr(int index)
        {
            return index switch
            {
                0 => RAX, 1 => RCX, 2 => RDX, 3 => RBX,
                4 => RSP, 5 => RBP, 6 => RSI, 7 => RDI,
                8 => R8, 9 => R9, 10 => R10, 11 => R11,
                12 => R12, 13 => R13, 14 => R14, 15 => R15,
                _ => 0
            };
        }

        /// <summary>
        /// Set a general-purpose register by index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetGpr(int index, ulong value)
        {
            switch (index)
            {
                case 0: RAX = value; break;
                case 1: RCX = value; break;
                case 2: RDX = value; break;
                case 3: RBX = value; break;
                case 4: RSP = value; break;
                case 5: RBP = value; break;
                case 6: RSI = value; break;
                case 7: RDI = value; break;
                case 8: R8 = value; break;
                case 9: R9 = value; break;
                case 10: R10 = value; break;
                case 11: R11 = value; break;
                case 12: R12 = value; break;
                case 13: R13 = value; break;
                case 14: R14 = value; break;
                case 15: R15 = value; break;
            }
        }

        /// <summary>
        /// Set a 32-bit register (zero-extends to 64-bit per AMD64).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetGpr32(int index, uint value)
        {
            SetGpr(index, value);
        }

        // Flag helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetFlag(X86Flags flag) => (RFLAGS & (ulong)flag) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFlag(X86Flags flag, bool value)
        {
            if (value)
                RFLAGS |= (ulong)flag;
            else
                RFLAGS &= ~(ulong)flag;
        }

        /// <summary>
        /// Update arithmetic flags (SF, ZF, PF) based on a 64-bit result.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateFlags64(ulong result)
        {
            SetFlag(X86Flags.ZF, result == 0);
            SetFlag(X86Flags.SF, (result & 0x8000000000000000UL) != 0);
            SetFlag(X86Flags.PF, ParityByte((byte)result));
        }

        /// <summary>
        /// Update arithmetic flags for a 32-bit result.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateFlags32(uint result)
        {
            SetFlag(X86Flags.ZF, result == 0);
            SetFlag(X86Flags.SF, (result & 0x80000000U) != 0);
            SetFlag(X86Flags.PF, ParityByte((byte)result));
        }

        /// <summary>
        /// Compute parity of the low byte (true = even parity).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ParityByte(byte b)
        {
            b ^= (byte)(b >> 4);
            b ^= (byte)(b >> 2);
            b ^= (byte)(b >> 1);
            return (b & 1) == 0;
        }

        /// <summary>
        /// Push a 64-bit value onto the stack.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(Memory.VirtualMemoryManager mem, ulong value)
        {
            RSP -= 8;
            mem.WriteUInt64(RSP, value);
        }

        /// <summary>
        /// Pop a 64-bit value from the stack.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Pop(Memory.VirtualMemoryManager mem)
        {
            ulong value = mem.ReadUInt64(RSP);
            RSP += 8;
            return value;
        }
    }
}
