// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Translated instruction block representation.
// Each basic block of x86_64 code is translated once into a C# delegate
// that directly manipulates the CpuState. This is the native translation
// approach: we convert machine code into managed code at the block level
// rather than interpreting instruction-by-instruction.

using System;

namespace LinuxBinaryTranslator.Cpu.Translation
{
    /// <summary>
    /// Delegate type for a translated basic block.
    /// The block operates on the CPU state and memory, advancing RIP
    /// and returning the address of the next block to execute.
    /// </summary>
    /// <param name="state">CPU register state</param>
    /// <param name="memory">Virtual memory manager</param>
    /// <returns>Address of next instruction block to execute</returns>
    public delegate ulong TranslatedBlock(CpuState state, Memory.VirtualMemoryManager memory);

    /// <summary>
    /// A cached translated block with metadata.
    /// </summary>
    public sealed class CachedBlock
    {
        /// <summary>
        /// Starting virtual address of this block in the original ELF binary.
        /// </summary>
        public ulong Address { get; }

        /// <summary>
        /// Size of the original x86_64 code for this block in bytes.
        /// </summary>
        public int OriginalSize { get; }

        /// <summary>
        /// The translated managed delegate that executes this block.
        /// </summary>
        public TranslatedBlock Execute { get; }

        /// <summary>
        /// Number of times this block has been executed (for profiling).
        /// </summary>
        public long ExecutionCount;

        public CachedBlock(ulong address, int originalSize, TranslatedBlock execute)
        {
            Address = address;
            OriginalSize = originalSize;
            Execute = execute;
        }
    }

    /// <summary>
    /// Result of decoding a single x86_64 instruction during translation.
    /// </summary>
    public sealed class DecodedInstruction
    {
        public ulong Address { get; set; }
        public int Length { get; set; }

        // Decoded fields
        public byte[] RexPrefix { get; set; } = Array.Empty<byte>();
        public bool HasRex { get; set; }
        public bool RexW { get; set; }   // 64-bit operand size
        public bool RexR { get; set; }   // ModRM reg extension
        public bool RexX { get; set; }   // SIB index extension
        public bool RexB { get; set; }   // ModRM r/m or SIB base extension

        // Legacy prefix tracking (needed for REP string ops and operand size)
        public bool HasRepPrefix { get; set; }      // F3 — REP/REPE prefix
        public bool HasRepnePrefix { get; set; }    // F2 — REPNE/REPNZ prefix
        public bool HasOperandOverride { get; set; } // 66 — operand size override
        public bool HasAddressOverride { get; set; } // 67 — address size override

        public byte SegmentOverridePrefix { get; set; }
        public byte[] Opcode { get; set; } = Array.Empty<byte>();
        public byte ModRM { get; set; }
        public bool HasModRM { get; set; }
        public byte SIB { get; set; }
        public bool HasSIB { get; set; }
        public long Displacement { get; set; }
        public int DisplacementSize { get; set; }
        public long Immediate { get; set; }
        public int ImmediateSize { get; set; }

        // Decoded ModRM fields
        public int Mod => (ModRM >> 6) & 3;
        public int Reg => ((ModRM >> 3) & 7) | (RexR ? 8 : 0);
        public int RM => (ModRM & 7) | (RexB ? 8 : 0);

        /// <summary>
        /// Whether this instruction is a block-terminating instruction
        /// (branch, call, return, syscall, etc.)
        /// </summary>
        public bool IsTerminator { get; set; }

        /// <summary>
        /// Whether this is a syscall instruction.
        /// </summary>
        public bool IsSyscall { get; set; }
    }
}
