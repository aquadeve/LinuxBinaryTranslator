// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Native block translator: translates x86_64 basic blocks into C# delegates.
// This is the core of the "native translation" approach — each basic block of
// machine code is decoded once and converted into a TranslatedBlock delegate
// that directly manipulates CpuState and VirtualMemoryManager without
// per-instruction decode overhead at runtime.

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using LinuxBinaryTranslator.Cpu.Translation;
using LinuxBinaryTranslator.Memory;

namespace LinuxBinaryTranslator.Cpu
{
    /// <summary>
    /// Translates x86_64 basic blocks from ELF binaries into cached C# delegates.
    /// Uses a translation cache so each block is only decoded once, then executed
    /// natively as managed code on subsequent visits.
    /// </summary>
    public sealed class BlockTranslator
    {
        private readonly VirtualMemoryManager _memory;
        private readonly InstructionDecoder _decoder;
        private readonly ConcurrentDictionary<ulong, CachedBlock> _cache;

        /// <summary>
        /// Callback invoked when a syscall instruction is encountered.
        /// The syscall handler reads arguments from CpuState per the Linux ABI
        /// (RAX=syscall number, RDI/RSI/RDX/R10/R8/R9=args).
        /// </summary>
        public Func<CpuState, VirtualMemoryManager, long>? SyscallHandler { get; set; }

        public BlockTranslator(VirtualMemoryManager memory)
        {
            _memory = memory;
            _decoder = new InstructionDecoder(memory);
            _cache = new ConcurrentDictionary<ulong, CachedBlock>();
        }

        /// <summary>
        /// Get or translate the block starting at the given address.
        /// </summary>
        public CachedBlock GetBlock(ulong address)
        {
            return _cache.GetOrAdd(address, addr => TranslateBlock(addr));
        }

        /// <summary>
        /// Invalidate all cached translations (needed if code is modified).
        /// </summary>
        public void InvalidateCache()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Best-effort diagnostic dump of a decoded block.
        /// </summary>
        public string DescribeBlock(ulong startAddress, int maxInstructions = 8)
        {
            var sb = new StringBuilder();
            ulong addr = startAddress;

            for (int i = 0; i < maxInstructions && _memory.IsMapped(addr); i++)
            {
                var inst = _decoder.Decode(addr);
                if (inst.Length <= 0)
                    break;

                if (sb.Length > 0)
                    sb.Append(" | ");

                sb.Append($"0x{addr:X16}: {FormatBytes(_memory.Read(addr, (ulong)inst.Length))} len={inst.Length} op={FormatOpcode(inst.Opcode)}");
                if (inst.HasModRM)
                    sb.Append($" modrm={inst.ModRM:X2}");
                if (inst.DisplacementSize != 0)
                    sb.Append($" disp={inst.Displacement}");
                if (inst.ImmediateSize != 0)
                    sb.Append($" imm=0x{unchecked((ulong)inst.Immediate):X}");
                if (inst.IsTerminator)
                    sb.Append(" term");
                if (inst.IsSyscall)
                    sb.Append(" syscall");

                addr += (ulong)inst.Length;
                if (inst.IsTerminator || inst.IsSyscall)
                    break;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Translate a basic block starting at the given address.
        /// Decodes instructions until a terminator (branch, call, ret, syscall)
        /// is found, then builds a single delegate that executes the entire block.
        /// </summary>
        private CachedBlock TranslateBlock(ulong startAddress)
        {
            var instructions = new System.Collections.Generic.List<DecodedInstruction>();
            ulong addr = startAddress;
            const int MaxBlockSize = 256; // Safety limit

            // Decode instructions until we hit a block terminator
            for (int i = 0; i < MaxBlockSize; i++)
            {
                if (!_memory.IsMapped(addr))
                    break;

                var inst = _decoder.Decode(addr);
                instructions.Add(inst);
                addr += (ulong)inst.Length;

                if (inst.IsTerminator || inst.IsSyscall)
                    break;
            }

            if (instructions.Count == 0)
            {
                // Empty block — return a halt block
                return new CachedBlock(startAddress, 0, (state, mem) =>
                {
                    state.Halted = true;
                    return 0;
                });
            }

            int blockSize = (int)(addr - startAddress);
            var captured = instructions.ToArray();

            // Build a delegate that executes all instructions in this block
            TranslatedBlock blockDelegate = (state, mem) =>
            {
                for (int i = 0; i < captured.Length; i++)
                {
                    ulong next = ExecuteInstruction(captured[i], state, mem);
                    if (state.Halted)
                        return 0;
                    if (next != 0)
                        return next; // Branch/call/ret — next block address
                }
                // Fall through to the next sequential block
                return startAddress + (ulong)blockSize;
            };

            return new CachedBlock(startAddress, blockSize, blockDelegate);
        }

        /// <summary>
        /// Execute a single decoded instruction, modifying CPU state and memory.
        /// Returns 0 to continue sequential execution, or a non-zero address
        /// for control flow transfers (branch, call, ret).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ExecuteInstruction(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem)
        {
            ulong nextAddr = inst.Address + (ulong)inst.Length;

            // Handle syscall first
            if (inst.IsSyscall)
            {
                if (SyscallHandler != null)
                {
                    long result = SyscallHandler(state, mem);
                    state.RAX = (ulong)result;
                }
                state.RIP = nextAddr;
                return nextAddr;
            }

            byte opcode = inst.Opcode[0];

            // === REP/REPNE prefix handling for string instructions ===
            // REP (F3): Repeat while RCX != 0 (MOVS, STOS, LODS, INS, OUTS)
            // REPE (F3): Repeat while RCX != 0 && ZF=1 (CMPS, SCAS)
            // REPNE (F2): Repeat while RCX != 0 && ZF=0 (CMPS, SCAS)
            if ((inst.HasRepPrefix || inst.HasRepnePrefix) && IsStringOpcode(opcode))
            {
                return ExecuteRepStringOp(inst, opcode, state, mem, nextAddr);
            }

            // Two-byte opcodes
            if (opcode == 0x0F && inst.Opcode.Length > 1)
            {
                return ExecuteTwoByteInstruction(inst, state, mem, nextAddr);
            }

            return ExecuteOneByteInstruction(inst, opcode, state, mem, nextAddr);
        }

        /// <summary>
        /// Returns true if the opcode is a string instruction that can be REP-prefixed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsStringOpcode(byte opcode)
        {
            return opcode == 0xA4 || opcode == 0xA5   // MOVSB / MOVSD/MOVSQ
                || opcode == 0xAA || opcode == 0xAB   // STOSB / STOSD/STOSQ
                || opcode == 0xAC || opcode == 0xAD   // LODSB / LODSD/LODSQ
                || opcode == 0xA6 || opcode == 0xA7   // CMPSB / CMPSD/CMPSQ
                || opcode == 0xAE || opcode == 0xAF;  // SCASB / SCASD/SCASQ
        }

        /// <summary>
        /// Execute a string operation with REP/REPE/REPNE prefix.
        /// Loops based on RCX counter and (for CMPS/SCAS) the Zero Flag.
        /// This is critical for memcpy, memset, strlen and similar patterns
        /// used in static C library code.
        /// </summary>
        private ulong ExecuteRepStringOp(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            // CMPS and SCAS check ZF for REPE/REPNE termination
            bool isCmpsOrScas = opcode == 0xA6 || opcode == 0xA7 || opcode == 0xAE || opcode == 0xAF;

            // Safety limit to prevent infinite loops from buggy binaries
            const int MaxIterations = 0x10000000; // 256M iterations max

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                // Check RCX first — if 0, done before executing
                if (state.RCX == 0)
                    break;

                // Decrement RCX
                state.RCX--;

                // Execute one iteration of the string operation
                ExecuteOneByteInstruction(inst, opcode, state, mem, nextAddr);

                // For CMPS/SCAS with REPE (F3): stop if ZF=0
                // For CMPS/SCAS with REPNE (F2): stop if ZF=1
                if (isCmpsOrScas)
                {
                    bool zf = state.GetFlag(X86Flags.ZF);
                    if (inst.HasRepPrefix && !zf)   // REPE: stop when not equal
                        break;
                    if (inst.HasRepnePrefix && zf)   // REPNE: stop when equal
                        break;
                }
            }

            return 0; // Continue sequential execution
        }

        private ulong ExecuteOneByteInstruction(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            switch (opcode)
            {
                // NOP
                case 0x90:
                    return 0;

                // PUSH r64 (50+rd)
                case 0x50: case 0x51: case 0x52: case 0x53:
                case 0x54: case 0x55: case 0x56: case 0x57:
                {
                    int reg = (opcode - 0x50) | (inst.RexB ? 8 : 0);
                    state.Push(mem, state.GetGpr(reg));
                    return 0;
                }

                // POP r64 (58+rd)
                case 0x58: case 0x59: case 0x5A: case 0x5B:
                case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                {
                    int reg = (opcode - 0x58) | (inst.RexB ? 8 : 0);
                    state.SetGpr(reg, state.Pop(mem));
                    return 0;
                }

                // PUSH imm8 (6A) / PUSH imm32 (68)
                case 0x6A: case 0x68:
                    state.Push(mem, (ulong)inst.Immediate);
                    return 0;

                // MOV r/m, r (89) — 32/64-bit
                case 0x89:
                {
                    ulong val = state.GetGpr(inst.Reg);
                    if (inst.Mod == 3)
                    {
                        if (inst.RexW)
                            state.SetGpr(inst.RM, val);
                        else
                            state.SetGpr32(inst.RM, (uint)val);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        if (inst.RexW)
                            mem.WriteUInt64(addr, val);
                        else
                            mem.WriteUInt32(addr, (uint)val);
                    }
                    return 0;
                }

                // MOV r/m8, r8 (88)
                case 0x88:
                {
                    byte val = (byte)state.GetGpr(inst.Reg);
                    if (inst.Mod == 3)
                        SetRegByte(state, inst.RM, inst.HasRex, val);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        mem.WriteByte(addr, val);
                    }
                    return 0;
                }

                // MOV r, r/m (8B) — 32/64-bit
                case 0x8B:
                {
                    ulong val;
                    if (inst.Mod == 3)
                        val = state.GetGpr(inst.RM);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        val = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr);
                    }
                    if (inst.RexW)
                        state.SetGpr(inst.Reg, val);
                    else
                        state.SetGpr32(inst.Reg, (uint)val);
                    return 0;
                }

                // MOV r8, r/m8 (8A)
                case 0x8A:
                {
                    byte val;
                    if (inst.Mod == 3)
                        val = (byte)state.GetGpr(inst.RM);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        val = mem.ReadByte(addr);
                    }
                    SetRegByte(state, inst.Reg, inst.HasRex, val);
                    return 0;
                }

                // MOV r/m, imm32/64 (C7)
                case 0xC7:
                {
                    if (inst.Mod == 3)
                    {
                        if (inst.RexW)
                            state.SetGpr(inst.RM, (ulong)(long)inst.Immediate);
                        else
                            state.SetGpr32(inst.RM, (uint)inst.Immediate);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        if (inst.RexW)
                            mem.WriteUInt64(addr, (ulong)(long)inst.Immediate);
                        else
                            mem.WriteUInt32(addr, (uint)inst.Immediate);
                    }
                    return 0;
                }

                // MOV r/m8, imm8 (C6)
                case 0xC6:
                {
                    if (inst.Mod == 3)
                        SetRegByte(state, inst.RM, inst.HasRex, (byte)inst.Immediate);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        mem.WriteByte(addr, (byte)inst.Immediate);
                    }
                    return 0;
                }

                // MOV r64, imm64 / MOV r32, imm32 (B8+rd)
                case 0xB8: case 0xB9: case 0xBA: case 0xBB:
                case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                {
                    int reg = (opcode - 0xB8) | (inst.RexB ? 8 : 0);
                    if (inst.RexW)
                        state.SetGpr(reg, (ulong)inst.Immediate);
                    else
                        state.SetGpr32(reg, (uint)inst.Immediate);
                    return 0;
                }

                // MOV r8, imm8 (B0+rb)
                case 0xB0: case 0xB1: case 0xB2: case 0xB3:
                case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                {
                    int reg = (opcode - 0xB0) | (inst.RexB ? 8 : 0);
                    SetRegByte(state, reg, inst.HasRex, (byte)inst.Immediate);
                    return 0;
                }

                // LEA r, m (8D)
                case 0x8D:
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    if (inst.RexW)
                        state.SetGpr(inst.Reg, addr);
                    else
                        state.SetGpr32(inst.Reg, (uint)addr);
                    return 0;
                }

                // ALU operations: ADD/ADC/SBB/OR/AND/SUB/XOR/CMP
                case 0x01: case 0x09: case 0x21: case 0x29: case 0x31: case 0x39:
                case 0x11: case 0x19:
                    return ExecuteAluRmR(inst, opcode, state, mem, nextAddr, false);
                case 0x03: case 0x0B: case 0x23: case 0x2B: case 0x33: case 0x3B:
                case 0x13: case 0x1B:
                    return ExecuteAluRRm(inst, opcode, state, mem, nextAddr, false);
                case 0x00: case 0x08: case 0x20: case 0x28: case 0x30: case 0x38:
                case 0x10: case 0x18:
                    return ExecuteAluRmR(inst, opcode, state, mem, nextAddr, true);
                case 0x02: case 0x0A: case 0x22: case 0x2A: case 0x32: case 0x3A:
                case 0x12: case 0x1A:
                    return ExecuteAluRRm(inst, opcode, state, mem, nextAddr, true);

                // ALU AL/RAX, imm
                case 0x04: // ADD AL, imm8
                    state.AL = DoAlu8(0, state.AL, (byte)inst.Immediate, state);
                    return 0;
                case 0x05: // ADD RAX/EAX, imm32
                    if (inst.RexW)
                        state.RAX = DoAlu64(0, state.RAX, (ulong)(long)inst.Immediate, state);
                    else
                        state.EAX = DoAlu32(0, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x2C: // SUB AL, imm8
                    state.AL = DoAlu8(5, state.AL, (byte)inst.Immediate, state);
                    return 0;
                case 0x2D: // SUB RAX/EAX, imm32
                    if (inst.RexW)
                        state.RAX = DoAlu64(5, state.RAX, (ulong)(long)inst.Immediate, state);
                    else
                        state.EAX = DoAlu32(5, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x0C: state.AL = DoAlu8(1, state.AL, (byte)inst.Immediate, state); return 0; // OR AL
                case 0x0D: // OR EAX/RAX
                    if (inst.RexW) state.RAX = DoAlu64(1, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(1, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x24: state.AL = DoAlu8(4, state.AL, (byte)inst.Immediate, state); return 0; // AND AL
                case 0x25: // AND EAX/RAX
                    if (inst.RexW) state.RAX = DoAlu64(4, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(4, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x34: state.AL = DoAlu8(6, state.AL, (byte)inst.Immediate, state); return 0; // XOR AL
                case 0x35: // XOR EAX/RAX
                    if (inst.RexW) state.RAX = DoAlu64(6, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(6, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x3C: DoAlu8(7, state.AL, (byte)inst.Immediate, state); return 0; // CMP AL
                case 0x3D: // CMP EAX/RAX
                    if (inst.RexW) DoAlu64(7, state.RAX, (ulong)(long)inst.Immediate, state);
                    else DoAlu32(7, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x1C: state.AL = DoAlu8(3, state.AL, (byte)inst.Immediate, state); return 0; // SBB AL
                case 0x1D:
                    if (inst.RexW) state.RAX = DoAlu64(3, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(3, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x14: state.AL = DoAlu8(2, state.AL, (byte)inst.Immediate, state); return 0; // ADC AL
                case 0x15:
                    if (inst.RexW) state.RAX = DoAlu64(2, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(2, state.EAX, (uint)inst.Immediate, state);
                    return 0;

                // Group 1: ADD/OR/ADC/SBB/AND/SUB/XOR/CMP r/m, imm (81/83)
                case 0x80: case 0x81: case 0x83:
                    return ExecuteGroup1(inst, opcode, state, mem, nextAddr);

                // TEST r/m, r (84/85)
                case 0x84:
                {
                    byte a = (inst.Mod == 3) ? (byte)state.GetGpr(inst.RM) : mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    byte b = (byte)state.GetGpr(inst.Reg);
                    byte result = (byte)(a & b);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 0x85:
                {
                    ulong a, b;
                    if (inst.Mod == 3)
                        a = state.GetGpr(inst.RM);
                    else
                        a = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    b = state.GetGpr(inst.Reg);
                    ulong result = a & b;
                    if (inst.RexW) state.UpdateFlags64(result);
                    else state.UpdateFlags32((uint)result);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }

                // TEST AL/RAX, imm (A8/A9)
                case 0xA8:
                {
                    byte result = (byte)(state.AL & (byte)inst.Immediate);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 0xA9:
                {
                    if (inst.RexW)
                    {
                        ulong result = state.RAX & (ulong)(long)inst.Immediate;
                        state.UpdateFlags64(result);
                    }
                    else
                    {
                        uint result = state.EAX & (uint)inst.Immediate;
                        state.UpdateFlags32(result);
                    }
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }

                // XCHG r, r/m (87)
                case 0x87:
                {
                    ulong a = state.GetGpr(inst.Reg);
                    ulong b;
                    if (inst.Mod == 3)
                    {
                        b = state.GetGpr(inst.RM);
                        state.SetGpr(inst.RM, a);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        b = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr);
                        if (inst.RexW) mem.WriteUInt64(addr, a);
                        else mem.WriteUInt32(addr, (uint)a);
                    }
                    if (inst.RexW) state.SetGpr(inst.Reg, b);
                    else state.SetGpr32(inst.Reg, (uint)b);
                    return 0;
                }

                // Shift group (C1 r/m, imm8)
                case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                    return ExecuteShiftGroup(inst, opcode, state, mem, nextAddr);

                // INC/DEC/CALL/JMP/PUSH — group FF
                case 0xFF:
                    return ExecuteGroupFF(inst, state, mem, nextAddr);

                // NOT/NEG/MUL/IMUL/DIV/IDIV (F7)
                case 0xF7:
                    return ExecuteGroupF7(inst, state, mem, nextAddr);
                case 0xF6:
                    return ExecuteGroupF6(inst, state, mem, nextAddr);

                // IMUL r, r/m, imm (69/6B)
                case 0x69: case 0x6B:
                {
                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    long result = (long)src * inst.Immediate;
                    if (inst.RexW) state.SetGpr(inst.Reg, (ulong)result);
                    else state.SetGpr32(inst.Reg, (uint)result);
                    return 0;
                }

                // RET (C3)
                case 0xC3:
                {
                    ulong retAddr = state.Pop(mem);
                    state.RIP = retAddr;
                    return retAddr;
                }

                // RET imm16 (C2)
                case 0xC2:
                {
                    ulong retAddr = state.Pop(mem);
                    state.RSP += (ulong)inst.Immediate;
                    state.RIP = retAddr;
                    return retAddr;
                }

                // LEAVE (C9)
                case 0xC9:
                    state.RSP = state.RBP;
                    state.RBP = state.Pop(mem);
                    return 0;

                // JMP rel8 (EB) / JMP rel32 (E9)
                case 0xEB: case 0xE9:
                {
                    ulong target = nextAddr + (ulong)(long)inst.Immediate;
                    state.RIP = target;
                    return target;
                }

                // CALL rel32 (E8)
                case 0xE8:
                {
                    state.Push(mem, nextAddr);
                    ulong target = nextAddr + (ulong)(long)inst.Immediate;
                    state.RIP = target;
                    return target;
                }

                // Jcc rel8 (70-7F)
                case 0x70: case 0x71: case 0x72: case 0x73:
                case 0x74: case 0x75: case 0x76: case 0x77:
                case 0x78: case 0x79: case 0x7A: case 0x7B:
                case 0x7C: case 0x7D: case 0x7E: case 0x7F:
                {
                    int cc = opcode - 0x70;
                    if (EvaluateCondition(cc, state))
                    {
                        ulong target = nextAddr + (ulong)(long)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // INT 0x80 (CD 80) — 32-bit Linux syscall ABI
                case 0xCD:
                    if (inst.Immediate == 0x80 && SyscallHandler != null)
                    {
                        long result = SyscallHandler(state, mem);
                        state.RAX = (ulong)result;
                    }
                    state.RIP = nextAddr;
                    return nextAddr;

                // CBW/CWDE/CDQE (98)
                case 0x98:
                    if (inst.RexW)
                        state.RAX = (ulong)(long)(int)state.EAX; // CDQE
                    else
                        state.EAX = (uint)(int)(short)state.AX; // CWDE
                    return 0;

                // CWD/CDQ/CQO (99)
                case 0x99:
                    if (inst.RexW)
                        state.RDX = ((long)state.RAX < 0) ? 0xFFFFFFFFFFFFFFFFUL : 0; // CQO
                    else
                        state.EDX = ((int)state.EAX < 0) ? 0xFFFFFFFFU : 0; // CDQ
                    return 0;

                // CLC (F8), STC (F9), CMC (F5)
                case 0xF8: state.SetFlag(X86Flags.CF, false); return 0;
                case 0xF9: state.SetFlag(X86Flags.CF, true); return 0;
                case 0xF5: state.SetFlag(X86Flags.CF, !state.GetFlag(X86Flags.CF)); return 0;

                // CLD (FC), STD (FD)
                case 0xFC: state.SetFlag(X86Flags.DF, false); return 0;
                case 0xFD: state.SetFlag(X86Flags.DF, true); return 0;

                // STOSB/STOSQ (AA/AB) — store AL/RAX at [RDI], advance RDI
                case 0xAA:
                    mem.WriteByte(state.RDI, state.AL);
                    state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-1L) : 1;
                    return 0;
                case 0xAB:
                    if (inst.RexW)
                    {
                        mem.WriteUInt64(state.RDI, state.RAX);
                        state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-8L) : 8;
                    }
                    else
                    {
                        mem.WriteUInt32(state.RDI, state.EAX);
                        state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-4L) : 4;
                    }
                    return 0;

                // MOVSB/MOVSQ (A4/A5) — move [RSI] to [RDI]
                case 0xA4:
                    mem.WriteByte(state.RDI, mem.ReadByte(state.RSI));
                    if (state.GetFlag(X86Flags.DF)) { state.RSI--; state.RDI--; }
                    else { state.RSI++; state.RDI++; }
                    return 0;
                case 0xA5:
                    if (inst.RexW)
                    {
                        mem.WriteUInt64(state.RDI, mem.ReadUInt64(state.RSI));
                        if (state.GetFlag(X86Flags.DF)) { state.RSI -= 8; state.RDI -= 8; }
                        else { state.RSI += 8; state.RDI += 8; }
                    }
                    else
                    {
                        mem.WriteUInt32(state.RDI, mem.ReadUInt32(state.RSI));
                        if (state.GetFlag(X86Flags.DF)) { state.RSI -= 4; state.RDI -= 4; }
                        else { state.RSI += 4; state.RDI += 4; }
                    }
                    return 0;

                // HLT (F4)
                case 0xF4:
                    state.Halted = true;
                    return 0;

                // PUSHFQ (9C) — push RFLAGS
                case 0x9C:
                {
                    ulong rflags = 0;
                    if (state.GetFlag(X86Flags.CF)) rflags |= 1 << 0;
                    rflags |= 1 << 1; // Reserved, always 1
                    if (state.GetFlag(X86Flags.PF)) rflags |= 1 << 2;
                    if (state.GetFlag(X86Flags.AF)) rflags |= 1 << 4;
                    if (state.GetFlag(X86Flags.ZF)) rflags |= 1 << 6;
                    if (state.GetFlag(X86Flags.SF)) rflags |= 1 << 7;
                    if (state.GetFlag(X86Flags.DF)) rflags |= 1 << 10;
                    if (state.GetFlag(X86Flags.OF)) rflags |= 1 << 11;
                    state.Push(mem, rflags);
                    return 0;
                }

                // POPFQ (9D) — pop RFLAGS
                case 0x9D:
                {
                    ulong rflags = state.Pop(mem);
                    state.SetFlag(X86Flags.CF, (rflags & (1 << 0)) != 0);
                    state.SetFlag(X86Flags.PF, (rflags & (1 << 2)) != 0);
                    state.SetFlag(X86Flags.AF, (rflags & (1 << 4)) != 0);
                    state.SetFlag(X86Flags.ZF, (rflags & (1 << 6)) != 0);
                    state.SetFlag(X86Flags.SF, (rflags & (1 << 7)) != 0);
                    state.SetFlag(X86Flags.DF, (rflags & (1 << 10)) != 0);
                    state.SetFlag(X86Flags.OF, (rflags & (1 << 11)) != 0);
                    return 0;
                }

                // SAHF (9E) — store AH into flags
                case 0x9E:
                {
                    byte ah = (byte)(state.RAX >> 8);
                    state.SetFlag(X86Flags.CF, (ah & (1 << 0)) != 0);
                    state.SetFlag(X86Flags.PF, (ah & (1 << 2)) != 0);
                    state.SetFlag(X86Flags.AF, (ah & (1 << 4)) != 0);
                    state.SetFlag(X86Flags.ZF, (ah & (1 << 6)) != 0);
                    state.SetFlag(X86Flags.SF, (ah & (1 << 7)) != 0);
                    return 0;
                }

                // LAHF (9F) — load AH from flags
                case 0x9F:
                {
                    byte ah = (byte)(1 << 1); // bit 1 always set
                    if (state.GetFlag(X86Flags.CF)) ah |= 1 << 0;
                    if (state.GetFlag(X86Flags.PF)) ah |= 1 << 2;
                    if (state.GetFlag(X86Flags.AF)) ah |= 1 << 4;
                    if (state.GetFlag(X86Flags.ZF)) ah |= 1 << 6;
                    if (state.GetFlag(X86Flags.SF)) ah |= 1 << 7;
                    state.RAX = (state.RAX & 0xFFFFFFFFFFFF00FFUL) | ((ulong)ah << 8);
                    return 0;
                }

                // XCHG r64, RAX (91-97)
                case 0x91: case 0x92: case 0x93:
                case 0x94: case 0x95: case 0x96: case 0x97:
                {
                    int reg = (opcode - 0x90) | (inst.RexB ? 8 : 0);
                    ulong tmp = state.RAX;
                    state.RAX = state.GetGpr(reg);
                    state.SetGpr(reg, tmp);
                    return 0;
                }

                // LOOP (E2): decrement RCX, jump if RCX != 0
                case 0xE2:
                {
                    state.RCX--;
                    if (state.RCX != 0)
                    {
                        ulong target = nextAddr + (ulong)(long)(sbyte)(byte)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // LOOPE/LOOPZ (E1): decrement RCX, jump if RCX != 0 && ZF=1
                case 0xE1:
                {
                    state.RCX--;
                    if (state.RCX != 0 && state.GetFlag(X86Flags.ZF))
                    {
                        ulong target = nextAddr + (ulong)(long)(sbyte)(byte)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // LOOPNE/LOOPNZ (E0): decrement RCX, jump if RCX != 0 && ZF=0
                case 0xE0:
                {
                    state.RCX--;
                    if (state.RCX != 0 && !state.GetFlag(X86Flags.ZF))
                    {
                        ulong target = nextAddr + (ulong)(long)(sbyte)(byte)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // JRCXZ (E3): jump if RCX == 0
                case 0xE3:
                {
                    if (state.RCX == 0)
                    {
                        ulong target = nextAddr + (ulong)(long)(sbyte)(byte)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // x87 FPU (D8-DF) — stub: no-op to prevent crashes
                // Most static CLI binaries don't rely heavily on x87; dynamic binaries
                // use SSE2 for float. We just advance past these instructions.
                case 0xD8: case 0xD9: case 0xDA: case 0xDB:
                case 0xDC: case 0xDD: case 0xDE: case 0xDF:
                    return 0;

                // MOVSXD r64, r/m32 (0x63 with REX.W) — sign-extend dword to qword
                case 0x63:
                {
                    int val;
                    if (inst.Mod == 3)
                        val = (int)(uint)state.GetGpr(inst.RM);
                    else
                        val = (int)mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW)
                        state.SetGpr(inst.Reg, (ulong)(long)val);
                    else
                        state.SetGpr32(inst.Reg, (uint)val);
                    return 0;
                }

                // XCHG r8, r/m8 (86)
                case 0x86:
                {
                    byte a = (byte)state.GetGpr(inst.Reg);
                    byte b;
                    if (inst.Mod == 3)
                    {
                        b = (byte)state.GetGpr(inst.RM);
                        SetRegByte(state, inst.RM, inst.HasRex, a);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        b = mem.ReadByte(addr);
                        mem.WriteByte(addr, a);
                    }
                    SetRegByte(state, inst.Reg, inst.HasRex, b);
                    return 0;
                }

                // MOV AL, moffs8 (A0)
                case 0xA0:
                {
                    ulong moffs = ApplySegmentBase(inst, state, (ulong)inst.Immediate);
                    state.AL = mem.ReadByte(moffs);
                    return 0;
                }

                // MOV RAX/EAX, moffs (A1)
                case 0xA1:
                {
                    ulong moffs = ApplySegmentBase(inst, state, (ulong)inst.Immediate);
                    if (inst.RexW)
                        state.RAX = mem.ReadUInt64(moffs);
                    else
                        state.EAX = mem.ReadUInt32(moffs);
                    return 0;
                }

                // MOV moffs8, AL (A2)
                case 0xA2:
                {
                    ulong moffs = ApplySegmentBase(inst, state, (ulong)inst.Immediate);
                    mem.WriteByte(moffs, state.AL);
                    return 0;
                }

                // MOV moffs, RAX/EAX (A3)
                case 0xA3:
                {
                    ulong moffs = ApplySegmentBase(inst, state, (ulong)inst.Immediate);
                    if (inst.RexW)
                        mem.WriteUInt64(moffs, state.RAX);
                    else
                        mem.WriteUInt32(moffs, state.EAX);
                    return 0;
                }

                // CMPSB (A6) — compare [RSI] with [RDI]
                case 0xA6:
                {
                    byte lhs = mem.ReadByte(state.RSI);
                    byte rhs = mem.ReadByte(state.RDI);
                    DoAlu8(7, lhs, rhs, state); // CMP sets flags
                    if (state.GetFlag(X86Flags.DF)) { state.RSI--; state.RDI--; }
                    else { state.RSI++; state.RDI++; }
                    return 0;
                }

                // CMPSQ/CMPSD (A7)
                case 0xA7:
                    if (inst.RexW)
                    {
                        ulong lhs = mem.ReadUInt64(state.RSI);
                        ulong rhs = mem.ReadUInt64(state.RDI);
                        DoAlu64(7, lhs, rhs, state);
                        if (state.GetFlag(X86Flags.DF)) { state.RSI -= 8; state.RDI -= 8; }
                        else { state.RSI += 8; state.RDI += 8; }
                    }
                    else
                    {
                        uint lhs = mem.ReadUInt32(state.RSI);
                        uint rhs = mem.ReadUInt32(state.RDI);
                        DoAlu32(7, lhs, rhs, state);
                        if (state.GetFlag(X86Flags.DF)) { state.RSI -= 4; state.RDI -= 4; }
                        else { state.RSI += 4; state.RDI += 4; }
                    }
                    return 0;

                // LODSB (AC) — load [RSI] into AL
                case 0xAC:
                    state.AL = mem.ReadByte(state.RSI);
                    state.RSI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-1L) : 1;
                    return 0;

                // LODSD/LODSQ (AD)
                case 0xAD:
                    if (inst.RexW)
                    {
                        state.RAX = mem.ReadUInt64(state.RSI);
                        state.RSI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-8L) : 8;
                    }
                    else
                    {
                        state.EAX = mem.ReadUInt32(state.RSI);
                        state.RSI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-4L) : 4;
                    }
                    return 0;

                // SCASB (AE) — compare AL with [RDI]
                case 0xAE:
                {
                    byte rhs = mem.ReadByte(state.RDI);
                    DoAlu8(7, state.AL, rhs, state);
                    state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-1L) : 1;
                    return 0;
                }

                // SCASD/SCASQ (AF)
                case 0xAF:
                    if (inst.RexW)
                    {
                        ulong rhs = mem.ReadUInt64(state.RDI);
                        DoAlu64(7, state.RAX, rhs, state);
                        state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-8L) : 8;
                    }
                    else
                    {
                        uint rhs = mem.ReadUInt32(state.RDI);
                        DoAlu32(7, state.EAX, rhs, state);
                        state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-4L) : 4;
                    }
                    return 0;

                // ENTER (C8)
                case 0xC8:
                {
                    ushort frameSize = (ushort)inst.Immediate;
                    state.Push(mem, state.RBP);
                    state.RBP = state.RSP;
                    state.RSP -= frameSize;
                    return 0;
                }

                default:
                    // Unimplemented — advance to next instruction
                    return 0;
            }
        }

        private ulong ExecuteTwoByteInstruction(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            byte second = inst.Opcode[1];

            switch (second)
            {
                // ENDBR64 / ENDBR32 (CET indirect branch landing pads)
                // These are no-ops for our emulator but must decode as 4 bytes.
                case 0x1E:
                    return 0;

                // Jcc rel32 (0F 80-8F)
                case 0x80: case 0x81: case 0x82: case 0x83:
                case 0x84: case 0x85: case 0x86: case 0x87:
                case 0x88: case 0x89: case 0x8A: case 0x8B:
                case 0x8C: case 0x8D: case 0x8E: case 0x8F:
                {
                    int cc = second - 0x80;
                    if (EvaluateCondition(cc, state))
                    {
                        ulong target = nextAddr + (ulong)(long)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // SETcc (0F 90-9F)
                case 0x90: case 0x91: case 0x92: case 0x93:
                case 0x94: case 0x95: case 0x96: case 0x97:
                case 0x98: case 0x99: case 0x9A: case 0x9B:
                case 0x9C: case 0x9D: case 0x9E: case 0x9F:
                {
                    int cc = second - 0x90;
                    byte val = EvaluateCondition(cc, state) ? (byte)1 : (byte)0;
                    if (inst.Mod == 3)
                        SetRegByte(state, inst.RM, inst.HasRex, val);
                    else
                        mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), val);
                    return 0;
                }

                // CMOVcc (0F 40-4F)
                case 0x40: case 0x41: case 0x42: case 0x43:
                case 0x44: case 0x45: case 0x46: case 0x47:
                case 0x48: case 0x49: case 0x4A: case 0x4B:
                case 0x4C: case 0x4D: case 0x4E: case 0x4F:
                {
                    int cc = second - 0x40;
                    if (EvaluateCondition(cc, state))
                    {
                        ulong val;
                        if (inst.Mod == 3) val = state.GetGpr(inst.RM);
                        else val = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                        if (inst.RexW) state.SetGpr(inst.Reg, val);
                        else state.SetGpr32(inst.Reg, (uint)val);
                    }
                    return 0;
                }

                // MOVZX r, r/m8 (0F B6)
                case 0xB6:
                {
                    byte val;
                    if (inst.Mod == 3)
                        val = (byte)state.GetGpr(inst.RM);
                    else
                        val = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, val);
                    else state.SetGpr32(inst.Reg, val);
                    return 0;
                }

                // MOVZX r, r/m16 (0F B7)
                case 0xB7:
                {
                    ushort val;
                    if (inst.Mod == 3)
                        val = (ushort)state.GetGpr(inst.RM);
                    else
                        val = mem.ReadUInt16(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, val);
                    else state.SetGpr32(inst.Reg, val);
                    return 0;
                }

                // MOVSX r, r/m8 (0F BE)
                case 0xBE:
                {
                    sbyte val;
                    if (inst.Mod == 3)
                        val = (sbyte)(byte)state.GetGpr(inst.RM);
                    else
                        val = (sbyte)mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, (ulong)(long)val);
                    else state.SetGpr32(inst.Reg, (uint)(int)val);
                    return 0;
                }

                // MOVSX r, r/m16 (0F BF)
                case 0xBF:
                {
                    short val;
                    if (inst.Mod == 3)
                        val = (short)(ushort)state.GetGpr(inst.RM);
                    else
                        val = (short)mem.ReadUInt16(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, (ulong)(long)val);
                    else state.SetGpr32(inst.Reg, (uint)(int)val);
                    return 0;
                }

                // IMUL r, r/m (0F AF)
                case 0xAF:
                {
                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW)
                    {
                        long a = (long)state.GetGpr(inst.Reg);
                        long b = (long)src;
                        long result = a * b;
                        state.SetGpr(inst.Reg, (ulong)result);
                        // CF=OF=1 if result doesn't fit in 64 bits (i.e., high 64 bits are not sign extension)
                        UInt128Multiply((ulong)a, (ulong)b, out _, out ulong hi);
                        bool overflow = (result >= 0) ? (hi != 0) : (hi != 0xFFFFFFFFFFFFFFFFUL);
                        state.SetFlag(X86Flags.CF, overflow);
                        state.SetFlag(X86Flags.OF, overflow);
                    }
                    else
                    {
                        long a = (int)(uint)state.GetGpr(inst.Reg);
                        long b = (int)(uint)src;
                        long result64 = a * b;
                        int result = (int)result64;
                        state.SetGpr32(inst.Reg, (uint)result);
                        // CF=OF=1 if result doesn't fit in 32 bits
                        bool overflow = result64 != (long)result;
                        state.SetFlag(X86Flags.CF, overflow);
                        state.SetFlag(X86Flags.OF, overflow);
                    }
                    return 0;
                }

                // CPUID (0F A2) — return minimal identification
                case 0xA2:
                    ExecuteCpuid(state);
                    return 0;

                // RDTSC (0F 31) — return a monotonic counter
                case 0x31:
                {
                    ulong tsc = (ulong)Environment.TickCount * 1000000UL;
                    state.EAX = (uint)tsc;
                    state.EDX = (uint)(tsc >> 32);
                    return 0;
                }

                // Multi-byte NOP (0F 1F)
                case 0x1F:
                    return 0;

                // BSF — bit scan forward (0F BC)
                case 0xBC:
                {
                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (src == 0)
                    {
                        state.SetFlag(X86Flags.ZF, true);
                    }
                    else
                    {
                        state.SetFlag(X86Flags.ZF, false);
                        int bit = 0;
                        ulong tmp = src;
                        while ((tmp & 1) == 0) { tmp >>= 1; bit++; }
                        if (inst.RexW) state.SetGpr(inst.Reg, (ulong)bit);
                        else state.SetGpr32(inst.Reg, (uint)bit);
                    }
                    return 0;
                }

                // BSR — bit scan reverse (0F BD)
                case 0xBD:
                {
                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (src == 0)
                    {
                        state.SetFlag(X86Flags.ZF, true);
                    }
                    else
                    {
                        state.SetFlag(X86Flags.ZF, false);
                        int bit = inst.RexW ? 63 : 31;
                        ulong mask = inst.RexW ? 0x8000000000000000UL : 0x80000000UL;
                        while ((src & mask) == 0) { mask >>= 1; bit--; }
                        if (inst.RexW) state.SetGpr(inst.Reg, (ulong)bit);
                        else state.SetGpr32(inst.Reg, (uint)bit);
                    }
                    return 0;
                }

                // BT r/m, r (0F A3) — bit test
                case 0xA3:
                {
                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    int bit = (int)(state.GetGpr(inst.Reg) & (inst.RexW ? 63UL : 31UL));
                    state.SetFlag(X86Flags.CF, ((src >> bit) & 1) != 0);
                    return 0;
                }

                // BTS r/m, r (0F AB) — bit test and set
                case 0xAB:
                {
                    ulong src;
                    ulong addr = 0;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else { addr = ComputeEffectiveAddress(inst, state, mem, nextAddr); src = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr); }
                    int bit = (int)(state.GetGpr(inst.Reg) & (inst.RexW ? 63UL : 31UL));
                    state.SetFlag(X86Flags.CF, ((src >> bit) & 1) != 0);
                    src |= 1UL << bit;
                    if (inst.Mod == 3) { if (inst.RexW) state.SetGpr(inst.RM, src); else state.SetGpr32(inst.RM, (uint)src); }
                    else { if (inst.RexW) mem.WriteUInt64(addr, src); else mem.WriteUInt32(addr, (uint)src); }
                    return 0;
                }

                // BTR r/m, r (0F B3) — bit test and reset
                case 0xB3:
                {
                    ulong src;
                    ulong addr = 0;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else { addr = ComputeEffectiveAddress(inst, state, mem, nextAddr); src = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr); }
                    int bit = (int)(state.GetGpr(inst.Reg) & (inst.RexW ? 63UL : 31UL));
                    state.SetFlag(X86Flags.CF, ((src >> bit) & 1) != 0);
                    src &= ~(1UL << bit);
                    if (inst.Mod == 3) { if (inst.RexW) state.SetGpr(inst.RM, src); else state.SetGpr32(inst.RM, (uint)src); }
                    else { if (inst.RexW) mem.WriteUInt64(addr, src); else mem.WriteUInt32(addr, (uint)src); }
                    return 0;
                }

                // BTC r/m, r (0F BB) — bit test and complement
                case 0xBB:
                {
                    ulong src;
                    ulong addr = 0;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else { addr = ComputeEffectiveAddress(inst, state, mem, nextAddr); src = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr); }
                    int bit = (int)(state.GetGpr(inst.Reg) & (inst.RexW ? 63UL : 31UL));
                    state.SetFlag(X86Flags.CF, ((src >> bit) & 1) != 0);
                    src ^= 1UL << bit;
                    if (inst.Mod == 3) { if (inst.RexW) state.SetGpr(inst.RM, src); else state.SetGpr32(inst.RM, (uint)src); }
                    else { if (inst.RexW) mem.WriteUInt64(addr, src); else mem.WriteUInt32(addr, (uint)src); }
                    return 0;
                }

                // BT/BTS/BTR/BTC r/m, imm8 (0F BA /4-/7)
                case 0xBA:
                {
                    int subOp = (inst.ModRM >> 3) & 7;
                    ulong src;
                    ulong addr = 0;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else { addr = ComputeEffectiveAddress(inst, state, mem, nextAddr); src = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr); }
                    int bit = (int)(inst.Immediate & (inst.RexW ? 63 : 31));
                    state.SetFlag(X86Flags.CF, ((src >> bit) & 1) != 0);
                    if (subOp == 5) src |= 1UL << bit;        // BTS
                    else if (subOp == 6) src &= ~(1UL << bit); // BTR
                    else if (subOp == 7) src ^= 1UL << bit;    // BTC
                    // subOp == 4 is plain BT, no modification
                    if (subOp >= 5)
                    {
                        if (inst.Mod == 3) { if (inst.RexW) state.SetGpr(inst.RM, src); else state.SetGpr32(inst.RM, (uint)src); }
                        else { if (inst.RexW) mem.WriteUInt64(addr, src); else mem.WriteUInt32(addr, (uint)src); }
                    }
                    return 0;
                }

                // XADD r/m, r (0F C1)
                case 0xC1:
                {
                    ulong a, b = state.GetGpr(inst.Reg);
                    if (inst.Mod == 3)
                    {
                        a = state.GetGpr(inst.RM);
                        ulong sum = inst.RexW ? DoAlu64(0, a, b, state) : DoAlu32(0, (uint)a, (uint)b, state);
                        state.SetGpr(inst.Reg, a);
                        if (inst.RexW) state.SetGpr(inst.RM, sum); else state.SetGpr32(inst.RM, (uint)sum);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        a = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr);
                        ulong sum = inst.RexW ? DoAlu64(0, a, b, state) : DoAlu32(0, (uint)a, (uint)b, state);
                        state.SetGpr(inst.Reg, a);
                        if (inst.RexW) mem.WriteUInt64(addr, sum); else mem.WriteUInt32(addr, (uint)sum);
                    }
                    return 0;
                }

                // XADD r/m8, r8 (0F C0)
                case 0xC0:
                {
                    byte a, b = (byte)state.GetGpr(inst.Reg);
                    if (inst.Mod == 3)
                    {
                        a = (byte)state.GetGpr(inst.RM);
                        byte sum = DoAlu8(0, a, b, state);
                        SetRegByte(state, inst.Reg, inst.HasRex, a);
                        SetRegByte(state, inst.RM, inst.HasRex, sum);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        a = mem.ReadByte(addr);
                        byte sum = DoAlu8(0, a, b, state);
                        SetRegByte(state, inst.Reg, inst.HasRex, a);
                        mem.WriteByte(addr, sum);
                    }
                    return 0;
                }

                // CMPXCHG r/m, r (0F B1 — 32/64-bit)
                case 0xB1:
                {
                    ulong comparand = inst.RexW ? state.RAX : state.EAX;
                    ulong dest;
                    ulong addr = 0;
                    if (inst.Mod == 3) dest = state.GetGpr(inst.RM);
                    else { addr = ComputeEffectiveAddress(inst, state, mem, nextAddr); dest = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr); }

                    if (dest == comparand)
                    {
                        state.SetFlag(X86Flags.ZF, true);
                        ulong src = state.GetGpr(inst.Reg);
                        if (inst.Mod == 3) { if (inst.RexW) state.SetGpr(inst.RM, src); else state.SetGpr32(inst.RM, (uint)src); }
                        else { if (inst.RexW) mem.WriteUInt64(addr, src); else mem.WriteUInt32(addr, (uint)src); }
                    }
                    else
                    {
                        state.SetFlag(X86Flags.ZF, false);
                        if (inst.RexW) state.RAX = dest; else state.EAX = (uint)dest;
                    }
                    return 0;
                }

                // CMPXCHG r/m8, r8 (0F B0)
                case 0xB0:
                {
                    byte comparand = state.AL;
                    byte dest;
                    ulong addr = 0;
                    if (inst.Mod == 3) dest = (byte)state.GetGpr(inst.RM);
                    else { addr = ComputeEffectiveAddress(inst, state, mem, nextAddr); dest = mem.ReadByte(addr); }

                    if (dest == comparand)
                    {
                        state.SetFlag(X86Flags.ZF, true);
                        byte src = (byte)state.GetGpr(inst.Reg);
                        if (inst.Mod == 3) SetRegByte(state, inst.RM, inst.HasRex, src);
                        else mem.WriteByte(addr, src);
                    }
                    else
                    {
                        state.SetFlag(X86Flags.ZF, false);
                        state.AL = dest;
                    }
                    return 0;
                }

                // BSWAP (0F C8+rd) — byte swap
                case 0xC8: case 0xC9: case 0xCA: case 0xCB:
                case 0xCC: case 0xCD: case 0xCE: case 0xCF:
                {
                    int reg = (second - 0xC8) | (inst.RexB ? 8 : 0);
                    if (inst.RexW)
                    {
                        ulong val = state.GetGpr(reg);
                        val = ((val & 0xFF00000000000000UL) >> 56) |
                              ((val & 0x00FF000000000000UL) >> 40) |
                              ((val & 0x0000FF0000000000UL) >> 24) |
                              ((val & 0x000000FF00000000UL) >> 8) |
                              ((val & 0x00000000FF000000UL) << 8) |
                              ((val & 0x0000000000FF0000UL) << 24) |
                              ((val & 0x000000000000FF00UL) << 40) |
                              ((val & 0x00000000000000FFUL) << 56);
                        state.SetGpr(reg, val);
                    }
                    else
                    {
                        uint val = (uint)state.GetGpr(reg);
                        val = ((val >> 24) & 0xFF) |
                              ((val >> 8) & 0xFF00) |
                              ((val << 8) & 0xFF0000) |
                              ((val << 24) & 0xFF000000);
                        state.SetGpr32(reg, val);
                    }
                    return 0;
                }

                // POPCNT (F3 0F B8)
                case 0xB8:
                {
                    if (!inst.HasRepPrefix)
                        return 0;

                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    int count = 0;
                    ulong tmp = src;
                    while (tmp != 0) { count += (int)(tmp & 1); tmp >>= 1; }
                    if (inst.RexW) state.SetGpr(inst.Reg, (ulong)count);
                    else state.SetGpr32(inst.Reg, (uint)count);
                    state.SetFlag(X86Flags.ZF, src == 0);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    state.SetFlag(X86Flags.SF, false);
                    state.SetFlag(X86Flags.PF, false);
                    return 0;
                }

                // === SSE/SSE2 instruction execution ===
                // Basic support for common SSE instructions found even in non-FP
                // static binaries (memory init, zeroing, copying patterns).

                // XORPS xmm, xmm/m128 (0F 57) — commonly used to zero an XMM register
                case 0x57:
                {
                    int dstReg = inst.Reg;
                    if (inst.Mod == 3)
                    {
                        int srcReg = inst.RM;
                        if (srcReg == dstReg)
                        {
                            // XORPS xmm, xmm (same reg) = zero register
                            state.XmmLow[dstReg] = 0;
                            state.XmmHigh[dstReg] = 0;
                        }
                        else
                        {
                            state.XmmLow[dstReg] ^= state.XmmLow[srcReg];
                            state.XmmHigh[dstReg] ^= state.XmmHigh[srcReg];
                        }
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        state.XmmLow[dstReg] ^= mem.ReadUInt64(addr);
                        state.XmmHigh[dstReg] ^= mem.ReadUInt64(addr + 8);
                    }
                    return 0;
                }

                // PXOR xmm, xmm/m128 (66 0F EF) — same as XORPS for integer
                case 0xEF:
                {
                    int dstReg = inst.Reg;
                    if (inst.Mod == 3)
                    {
                        int srcReg = inst.RM;
                        if (srcReg == dstReg)
                        {
                            state.XmmLow[dstReg] = 0;
                            state.XmmHigh[dstReg] = 0;
                        }
                        else
                        {
                            state.XmmLow[dstReg] ^= state.XmmLow[srcReg];
                            state.XmmHigh[dstReg] ^= state.XmmHigh[srcReg];
                        }
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        state.XmmLow[dstReg] ^= mem.ReadUInt64(addr);
                        state.XmmHigh[dstReg] ^= mem.ReadUInt64(addr + 8);
                    }
                    return 0;
                }

                // MOVAPS/MOVUPS xmm, xmm/m128 (0F 28 / 0F 10) — load
                case 0x28: case 0x10:
                {
                    int dstReg = inst.Reg;
                    if (inst.Mod == 3)
                    {
                        state.XmmLow[dstReg] = state.XmmLow[inst.RM];
                        state.XmmHigh[dstReg] = state.XmmHigh[inst.RM];
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        state.XmmLow[dstReg] = mem.ReadUInt64(addr);
                        state.XmmHigh[dstReg] = mem.ReadUInt64(addr + 8);
                    }
                    return 0;
                }

                // MOVAPS/MOVUPS xmm/m128, xmm (0F 29 / 0F 11) — store
                case 0x29: case 0x11:
                {
                    int srcReg = inst.Reg;
                    if (inst.Mod == 3)
                    {
                        state.XmmLow[inst.RM] = state.XmmLow[srcReg];
                        state.XmmHigh[inst.RM] = state.XmmHigh[srcReg];
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        mem.WriteUInt64(addr, state.XmmLow[srcReg]);
                        mem.WriteUInt64(addr + 8, state.XmmHigh[srcReg]);
                    }
                    return 0;
                }

                // MOVDQA/MOVDQU xmm, xmm/m128 (66 0F 6F) — load
                case 0x6F:
                {
                    int dstReg = inst.Reg;
                    if (inst.Mod == 3)
                    {
                        state.XmmLow[dstReg] = state.XmmLow[inst.RM];
                        state.XmmHigh[dstReg] = state.XmmHigh[inst.RM];
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        state.XmmLow[dstReg] = mem.ReadUInt64(addr);
                        state.XmmHigh[dstReg] = mem.ReadUInt64(addr + 8);
                    }
                    return 0;
                }

                // MOVDQA/MOVDQU xmm/m128, xmm (66 0F 7F) — store
                case 0x7F:
                {
                    int srcReg = inst.Reg;
                    if (inst.Mod == 3)
                    {
                        state.XmmLow[inst.RM] = state.XmmLow[srcReg];
                        state.XmmHigh[inst.RM] = state.XmmHigh[srcReg];
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        mem.WriteUInt64(addr, state.XmmLow[srcReg]);
                        mem.WriteUInt64(addr + 8, state.XmmHigh[srcReg]);
                    }
                    return 0;
                }

                // MOVD xmm, r/m32 or MOVQ xmm, r/m64 (66 0F 6E) — GPR to XMM
                case 0x6E:
                {
                    int dstReg = inst.Reg;
                    if (inst.RexW)
                    {
                        ulong val = inst.Mod == 3 ? state.GetGpr(inst.RM) : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                        state.XmmLow[dstReg] = val;
                        state.XmmHigh[dstReg] = 0;
                    }
                    else
                    {
                        uint val = inst.Mod == 3 ? (uint)state.GetGpr(inst.RM) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                        state.XmmLow[dstReg] = val;
                        state.XmmHigh[dstReg] = 0;
                    }
                    return 0;
                }

                // MOVD r/m32, xmm or MOVQ r/m64, xmm (66 0F 7E) — XMM to GPR
                case 0x7E:
                {
                    int srcReg = inst.Reg;
                    if (inst.RexW)
                    {
                        if (inst.Mod == 3)
                            state.SetGpr(inst.RM, state.XmmLow[srcReg]);
                        else
                            mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), state.XmmLow[srcReg]);
                    }
                    else
                    {
                        if (inst.Mod == 3)
                            state.SetGpr32(inst.RM, (uint)state.XmmLow[srcReg]);
                        else
                            mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), (uint)state.XmmLow[srcReg]);
                    }
                    return 0;
                }

                // MOVNTDQ/MOVNTI/MOVNTPS (0F 2B/C3/E7) — non-temporal store (same as regular store for us)
                case 0x2B: case 0xE7:
                {
                    int srcReg = inst.Reg;
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    mem.WriteUInt64(addr, state.XmmLow[srcReg]);
                    mem.WriteUInt64(addr + 8, state.XmmHigh[srcReg]);
                    return 0;
                }
                case 0xC3: // MOVNTI m32/64, r32/64 — non-temporal store from GPR
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    if (inst.RexW)
                        mem.WriteUInt64(addr, state.GetGpr(inst.Reg));
                    else
                        mem.WriteUInt32(addr, (uint)state.GetGpr(inst.Reg));
                    return 0;
                }

                // LFENCE/MFENCE/SFENCE (0F AE /5-7) — memory fences (no-op in single-threaded)
                case 0xAE:
                    return 0;

                // UCOMISS xmm,xmm/m32 (0F 2E) / UCOMISD xmm,xmm/m64 (66 0F 2E)
                // Sets ZF/PF/CF based on float comparison; unordered sets all three.
                case 0x2E: case 0x2F:
                {
                    int dstReg = inst.Reg;
                    bool isDouble = inst.HasOperandOverride; // 66 prefix = double
                    double a, b;
                    if (isDouble)
                    {
                        a = UInt64ToDouble(state.XmmLow[dstReg]);
                        if (inst.Mod == 3)
                            b = UInt64ToDouble(state.XmmLow[inst.RM]);
                        else
                            b = UInt64ToDouble(mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)));
                    }
                    else
                    {
                        a = UInt32ToFloat((uint)state.XmmLow[dstReg]);
                        if (inst.Mod == 3)
                            b = UInt32ToFloat((uint)state.XmmLow[inst.RM]);
                        else
                            b = UInt32ToFloat((uint)(int)mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr)));
                    }
                    // Unordered (NaN)?
                    if (double.IsNaN(a) || double.IsNaN(b))
                    {
                        state.SetFlag(X86Flags.ZF, true);
                        state.SetFlag(X86Flags.PF, true);
                        state.SetFlag(X86Flags.CF, true);
                    }
                    else
                    {
                        state.SetFlag(X86Flags.ZF, a == b);
                        state.SetFlag(X86Flags.PF, false);
                        state.SetFlag(X86Flags.CF, a < b);
                    }
                    state.SetFlag(X86Flags.OF, false);
                    state.SetFlag(X86Flags.SF, false);
                    return 0;
                }

                // CVTSI2SS (F3 0F 2A), CVTSI2SD (F2 0F 2A)
                case 0x2A:
                {
                    int dstReg = inst.Reg;
                    long intVal = inst.RexW
                        ? (long)(inst.Mod == 3 ? state.GetGpr(inst.RM) : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)))
                        : (int)(inst.Mod == 3 ? (uint)state.GetGpr(inst.RM) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr)));
                    if (inst.HasRepPrefix) // F3 = CVTSI2SS
                    {
                        float f = (float)intVal;
                        state.XmmLow[dstReg] = (state.XmmLow[dstReg] & 0xFFFFFFFF00000000UL)
                                               | (uint)FloatToUInt32(f);
                    }
                    else // F2 = CVTSI2SD
                    {
                        double d = (double)intVal;
                        state.XmmLow[dstReg] = (ulong)DoubleToUInt64(d);
                        state.XmmHigh[dstReg] = 0;
                    }
                    return 0;
                }

                // CVTTSS2SI (F3 0F 2C), CVTTSD2SI (F2 0F 2C) — truncate to int
                // CVTSS2SI  (F3 0F 2D), CVTSD2SI  (F2 0F 2D) — round to int
                case 0x2C: case 0x2D:
                {
                    int dstReg = inst.Reg;
                    long result;
                    if (inst.HasRepPrefix) // F3 = SS (scalar single)
                    {
                        uint raw = inst.Mod == 3 ? (uint)state.XmmLow[inst.RM] : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                        float f = UInt32ToFloat(raw);
                        result = second == 0x2C ? (long)f : (long)Math.Round(f);
                    }
                    else // F2 = SD (scalar double)
                    {
                        ulong raw = inst.Mod == 3 ? state.XmmLow[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                        double d = UInt64ToDouble(raw);
                        result = second == 0x2C ? (long)d : (long)Math.Round(d);
                    }
                    if (inst.RexW) state.SetGpr(dstReg, (ulong)result);
                    else state.SetGpr32(dstReg, (uint)(int)result);
                    return 0;
                }

                // SQRTPS (0F 51), SQRTSS (F3 0F 51), SQRTPD (66 0F 51), SQRTSD (F2 0F 51)
                case 0x51:
                {
                    int dstReg = inst.Reg;
                    ulong srcLow = inst.Mod == 3 ? state.XmmLow[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    if (inst.HasRepnePrefix) // F2 = SQRTSD
                    {
                        double d = UInt64ToDouble(srcLow);
                        state.XmmLow[dstReg] = (ulong)DoubleToUInt64(Math.Sqrt(d));
                    }
                    else if (inst.HasRepPrefix) // F3 = SQRTSS
                    {
                        float f = UInt32ToFloat((uint)srcLow);
                        uint r = (uint)FloatToUInt32((float)Math.Sqrt(f));
                        state.XmmLow[dstReg] = (state.XmmLow[dstReg] & 0xFFFFFFFF00000000UL) | r;
                    }
                    else if (inst.HasOperandOverride) // 66 = SQRTPD (2x double)
                    {
                        double d0 = UInt64ToDouble(srcLow);
                        double d1 = UInt64ToDouble(srcHigh);
                        state.XmmLow[dstReg] = (ulong)DoubleToUInt64(Math.Sqrt(d0));
                        state.XmmHigh[dstReg] = (ulong)DoubleToUInt64(Math.Sqrt(d1));
                    }
                    else // SQRTPS (4x float)
                    {
                        ulong lo = SqrtPackedFloat32Pair((uint)(srcLow & 0xFFFFFFFF), (uint)(srcLow >> 32));
                        ulong hi = SqrtPackedFloat32Pair((uint)(srcHigh & 0xFFFFFFFF), (uint)(srcHigh >> 32));
                        state.XmmLow[dstReg] = lo;
                        state.XmmHigh[dstReg] = hi;
                    }
                    return 0;
                }

                // ANDPS (0F 54), ANDNPS (0F 55), ORPS (0F 56)
                case 0x54: // ANDPS
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg] &= srcLow;
                    state.XmmHigh[dstReg] &= srcHigh;
                    return 0;
                }
                case 0x55: // ANDNPS — dst = ~dst & src
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg] = ~state.XmmLow[dstReg] & srcLow;
                    state.XmmHigh[dstReg] = ~state.XmmHigh[dstReg] & srcHigh;
                    return 0;
                }
                case 0x56: // ORPS
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg] |= srcLow;
                    state.XmmHigh[dstReg] |= srcHigh;
                    return 0;
                }

                // ADDPS/ADDSS/ADDPD/ADDSD (0F 58), MULPS/MULSS/MULPD/MULSD (0F 59)
                // SUBPS/SUBSS/SUBPD/SUBSD (0F 5C), DIVPS/DIVSS/DIVPD/DIVSD (0F 5E)
                // MINPS/MINSS/MINPD/MINSD (0F 5D), MAXPS/MAXSS/MAXPD/MAXSD (0F 5F)
                case 0x58: case 0x59: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);

                    if (inst.HasRepnePrefix) // F2 = scalar double (SD)
                    {
                        double a = UInt64ToDouble(state.XmmLow[dstReg]);
                        double b = UInt64ToDouble(srcLow);
                        double r = second switch
                        {
                            0x58 => a + b, 0x59 => a * b, 0x5C => a - b,
                            0x5E => b != 0 ? a / b : double.NaN,
                            0x5D => Math.Min(a, b), 0x5F => Math.Max(a, b),
                            _ => a
                        };
                        state.XmmLow[dstReg] = (ulong)DoubleToUInt64(r);
                        // High bits of XmmLow are preserved for scalar ops
                    }
                    else if (inst.HasRepPrefix) // F3 = scalar single (SS)
                    {
                        float a = UInt32ToFloat((uint)state.XmmLow[dstReg]);
                        float b = UInt32ToFloat((uint)srcLow);
                        float r = second switch
                        {
                            0x58 => a + b, 0x59 => a * b, 0x5C => a - b,
                            0x5E => b != 0 ? a / b : float.NaN,
                            0x5D => (float)Math.Min(a, b), 0x5F => (float)Math.Max(a, b),
                            _ => a
                        };
                        state.XmmLow[dstReg] = (state.XmmLow[dstReg] & 0xFFFFFFFF00000000UL)
                                               | (uint)FloatToUInt32(r);
                    }
                    else if (inst.HasOperandOverride) // 66 = packed double (PD) — 2x double
                    {
                        double a0 = UInt64ToDouble(state.XmmLow[dstReg]);
                        double a1 = UInt64ToDouble(state.XmmHigh[dstReg]);
                        double b0 = UInt64ToDouble(srcLow);
                        double b1 = UInt64ToDouble(srcHigh);
                        (double r0, double r1) = second switch
                        {
                            0x58 => (a0+b0, a1+b1), 0x59 => (a0*b0, a1*b1),
                            0x5C => (a0-b0, a1-b1), 0x5E => (a0/b0, a1/b1),
                            0x5D => (Math.Min(a0,b0), Math.Min(a1,b1)),
                            0x5F => (Math.Max(a0,b0), Math.Max(a1,b1)),
                            _ => (a0, a1)
                        };
                        state.XmmLow[dstReg] = (ulong)DoubleToUInt64(r0);
                        state.XmmHigh[dstReg] = (ulong)DoubleToUInt64(r1);
                    }
                    else // PS = packed single (4x float)
                    {
                        state.XmmLow[dstReg] = ArithPackedFloat32Pair(second,
                            (uint)(state.XmmLow[dstReg] & 0xFFFFFFFF), (uint)(state.XmmLow[dstReg] >> 32),
                            (uint)(srcLow & 0xFFFFFFFF), (uint)(srcLow >> 32));
                        state.XmmHigh[dstReg] = ArithPackedFloat32Pair(second,
                            (uint)(state.XmmHigh[dstReg] & 0xFFFFFFFF), (uint)(state.XmmHigh[dstReg] >> 32),
                            (uint)(srcHigh & 0xFFFFFFFF), (uint)(srcHigh >> 32));
                    }
                    return 0;
                }

                // CVTSS2SD (F3 0F 5A): scalar float32 → float64
                // CVTSD2SS (F2 0F 5A): scalar float64 → float32
                case 0x5A:
                {
                    int dstReg = inst.Reg;
                    ulong srcLow = inst.Mod == 3 ? state.XmmLow[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.HasRepPrefix) // F3 = CVTSS2SD
                    {
                        float f = UInt32ToFloat((uint)srcLow);
                        state.XmmLow[dstReg] = (ulong)DoubleToUInt64((double)f);
                        state.XmmHigh[dstReg] = 0;
                    }
                    else if (inst.HasRepnePrefix) // F2 = CVTSD2SS
                    {
                        double d = UInt64ToDouble(srcLow);
                        uint r = (uint)FloatToUInt32((float)d);
                        state.XmmLow[dstReg] = (state.XmmLow[dstReg] & 0xFFFFFFFF00000000UL) | r;
                    }
                    return 0;
                }

                // === Integer SSE/SSE2 operations ===

                // PCMPEQB (66 0F 74), PCMPEQW (66 0F 75), PCMPEQD (66 0F 76)
                case 0x74: case 0x75: case 0x76:
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    if (second == 0x74) // PCMPEQB — 16 bytes
                    {
                        state.XmmLow[dstReg]  = PcmpeqB(state.XmmLow[dstReg], srcLow);
                        state.XmmHigh[dstReg] = PcmpeqB(state.XmmHigh[dstReg], srcHigh);
                    }
                    else if (second == 0x75) // PCMPEQW — 8 words
                    {
                        state.XmmLow[dstReg]  = PcmpeqW(state.XmmLow[dstReg], srcLow);
                        state.XmmHigh[dstReg] = PcmpeqW(state.XmmHigh[dstReg], srcHigh);
                    }
                    else // PCMPEQD — 4 dwords
                    {
                        state.XmmLow[dstReg]  = PcmpeqD(state.XmmLow[dstReg], srcLow);
                        state.XmmHigh[dstReg] = PcmpeqD(state.XmmHigh[dstReg], srcHigh);
                    }
                    return 0;
                }

                // PSHUFD xmm, xmm/m128, imm8 (66 0F 70)
                case 0x70:
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    byte imm = (byte)inst.Immediate;
                    uint[] s = new uint[4]
                    {
                        (uint)(srcLow & 0xFFFFFFFF), (uint)(srcLow >> 32),
                        (uint)(srcHigh & 0xFFFFFFFF), (uint)(srcHigh >> 32)
                    };
                    state.XmmLow[dstReg]  = s[imm & 3] | ((ulong)s[(imm >> 2) & 3] << 32);
                    state.XmmHigh[dstReg] = s[(imm >> 4) & 3] | ((ulong)s[(imm >> 6) & 3] << 32);
                    return 0;
                }

                // PAND (66 0F DB), PANDN (66 0F DF), POR (66 0F EB)
                case 0xDB: // PAND
                {
                    int dstReg = inst.Reg;
                    state.XmmLow[dstReg]  &= inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    state.XmmHigh[dstReg] &= inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    return 0;
                }
                case 0xDF: // PANDN — dst = ~dst & src
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = ~state.XmmLow[dstReg] & srcLow;
                    state.XmmHigh[dstReg] = ~state.XmmHigh[dstReg] & srcHigh;
                    return 0;
                }
                case 0xEB: // POR
                {
                    int dstReg = inst.Reg;
                    state.XmmLow[dstReg]  |= inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    state.XmmHigh[dstReg] |= inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    return 0;
                }

                // PADDQ (66 0F D4), PADDB (66 0F FC), PADDW (66 0F FD), PADDD (66 0F FE)
                case 0xD4: // PADDQ
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  += srcLow;
                    state.XmmHigh[dstReg] += srcHigh;
                    return 0;
                }
                case 0xFC: // PADDB — 16 bytes
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = AddBytes(state.XmmLow[dstReg], srcLow);
                    state.XmmHigh[dstReg] = AddBytes(state.XmmHigh[dstReg], srcHigh);
                    return 0;
                }
                case 0xFD: // PADDW — 8 words
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = AddWords(state.XmmLow[dstReg], srcLow);
                    state.XmmHigh[dstReg] = AddWords(state.XmmHigh[dstReg], srcHigh);
                    return 0;
                }
                case 0xFE: // PADDD — 4 dwords
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = AddDwords(state.XmmLow[dstReg], srcLow);
                    state.XmmHigh[dstReg] = AddDwords(state.XmmHigh[dstReg], srcHigh);
                    return 0;
                }

                // PSUBQ (66 0F FB), PSUBB (66 0F F8), PSUBW (66 0F F9), PSUBD (66 0F FA)
                case 0xFB: // PSUBQ
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  -= srcLow;
                    state.XmmHigh[dstReg] -= srcHigh;
                    return 0;
                }
                case 0xF8: // PSUBB
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = SubBytes(state.XmmLow[dstReg], srcLow);
                    state.XmmHigh[dstReg] = SubBytes(state.XmmHigh[dstReg], srcHigh);
                    return 0;
                }
                case 0xF9: // PSUBW
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = SubWords(state.XmmLow[dstReg], srcLow);
                    state.XmmHigh[dstReg] = SubWords(state.XmmHigh[dstReg], srcHigh);
                    return 0;
                }
                case 0xFA: // PSUBD
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = SubDwords(state.XmmLow[dstReg], srcLow);
                    state.XmmHigh[dstReg] = SubDwords(state.XmmHigh[dstReg], srcHigh);
                    return 0;
                }

                // PMOVMSKB (66 0F D7) — extract MSB of each byte into a 16-bit mask in GPR
                case 0xD7:
                {
                    int dstReg = inst.Reg;
                    int srcReg = inst.RM;
                    uint mask = 0;
                    ulong lo = state.XmmLow[srcReg];
                    ulong hi = state.XmmHigh[srcReg];
                    for (int i = 0; i < 8; i++)
                    {
                        if ((lo & (0x80UL << (i * 8))) != 0) mask |= (uint)(1 << i);
                        if ((hi & (0x80UL << (i * 8))) != 0) mask |= (uint)(1 << (i + 8));
                    }
                    state.SetGpr32(dstReg, mask);
                    return 0;
                }

                // PMINUB (66 0F DA) — minimum of packed unsigned bytes
                case 0xDA:
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    state.XmmLow[dstReg]  = PminubQword(state.XmmLow[dstReg], srcLow);
                    state.XmmHigh[dstReg] = PminubQword(state.XmmHigh[dstReg], srcHigh);
                    return 0;
                }

                // PUNPCKLBW (0x60), PUNPCKLWD (0x61), PUNPCKLDQ (0x62)
                // PUNPCKHBW (0x68), PUNPCKHWD (0x69), PUNPCKHDQ (0x6A), PUNPCKHQDQ (0x6D)
                // PUNPCKLQDQ (0x6C)
                case 0x60: case 0x61: case 0x62: case 0x6C: case 0x6D:
                case 0x68: case 0x69: case 0x6A:
                {
                    int dstReg = inst.Reg;
                    ulong srcLow  = inst.Mod == 3 ? state.XmmLow[inst.RM]  : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    ulong srcHigh = inst.Mod == 3 ? state.XmmHigh[inst.RM] : mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr) + 8);
                    (ulong resLow, ulong resHigh) = second switch
                    {
                        0x60 => PunpcklBW(state.XmmLow[dstReg], srcLow),
                        0x61 => PunpcklWD(state.XmmLow[dstReg], srcLow),
                        0x62 => PunpcklDQ(state.XmmLow[dstReg], srcLow),
                        0x6C => (state.XmmLow[dstReg], srcLow),           // PUNPCKLQDQ
                        0x6D => (state.XmmHigh[dstReg], srcHigh),          // PUNPCKHQDQ
                        0x68 => PunpckhBW(state.XmmHigh[dstReg], srcHigh),
                        0x69 => PunpckhWD(state.XmmHigh[dstReg], srcHigh),
                        0x6A => PunpckhDQ(state.XmmHigh[dstReg], srcHigh),
                        _ => (state.XmmLow[dstReg], state.XmmHigh[dstReg])
                    };
                    state.XmmLow[dstReg]  = resLow;
                    state.XmmHigh[dstReg] = resHigh;
                    return 0;
                }

                // PSRLx/PSLLx/PSRAx with imm8 (66 0F 71/72/73 /reg)
                case 0x71: case 0x72: case 0x73:
                {
                    int dstReg = inst.RM; // destination is /r field in reg field
                    int regOp = (inst.ModRM >> 3) & 7;
                    int count = (byte)inst.Immediate;
                    if (second == 0x71) // PSRLW/PSRAW/PSLLW
                    {
                        if (regOp == 2) // PSRLW
                        { state.XmmLow[dstReg] = ShiftWords(state.XmmLow[dstReg], count, false, false); state.XmmHigh[dstReg] = ShiftWords(state.XmmHigh[dstReg], count, false, false); }
                        else if (regOp == 4) // PSRAW
                        { state.XmmLow[dstReg] = ShiftWords(state.XmmLow[dstReg], count, false, true); state.XmmHigh[dstReg] = ShiftWords(state.XmmHigh[dstReg], count, false, true); }
                        else if (regOp == 6) // PSLLW
                        { state.XmmLow[dstReg] = ShiftWords(state.XmmLow[dstReg], count, true, false); state.XmmHigh[dstReg] = ShiftWords(state.XmmHigh[dstReg], count, true, false); }
                    }
                    else if (second == 0x72) // PSRLD/PSRAD/PSLLD
                    {
                        if (regOp == 2) // PSRLD
                        { state.XmmLow[dstReg] = ShiftDwords(state.XmmLow[dstReg], count, false, false); state.XmmHigh[dstReg] = ShiftDwords(state.XmmHigh[dstReg], count, false, false); }
                        else if (regOp == 4) // PSRAD
                        { state.XmmLow[dstReg] = ShiftDwords(state.XmmLow[dstReg], count, false, true); state.XmmHigh[dstReg] = ShiftDwords(state.XmmHigh[dstReg], count, false, true); }
                        else if (regOp == 6) // PSLLD
                        { state.XmmLow[dstReg] = ShiftDwords(state.XmmLow[dstReg], count, true, false); state.XmmHigh[dstReg] = ShiftDwords(state.XmmHigh[dstReg], count, true, false); }
                    }
                    else // 0x73: PSRLQ/PSLLQ/PSLLDQ/PSRLDQ
                    {
                        if (regOp == 2) // PSRLQ
                        {
                            state.XmmLow[dstReg]  = count >= 64 ? 0 : state.XmmLow[dstReg] >> count;
                            state.XmmHigh[dstReg] = count >= 64 ? 0 : state.XmmHigh[dstReg] >> count;
                        }
                        else if (regOp == 6) // PSLLQ
                        {
                            state.XmmLow[dstReg]  = count >= 64 ? 0 : state.XmmLow[dstReg] << count;
                            state.XmmHigh[dstReg] = count >= 64 ? 0 : state.XmmHigh[dstReg] << count;
                        }
                        else if (regOp == 3) // PSRLDQ — shift right by bytes
                        {
                            (state.XmmLow[dstReg], state.XmmHigh[dstReg]) =
                                PsrldqPslldq(state.XmmLow[dstReg], state.XmmHigh[dstReg], count, false);
                        }
                        else if (regOp == 7) // PSLLDQ — shift left by bytes
                        {
                            (state.XmmLow[dstReg], state.XmmHigh[dstReg]) =
                                PsrldqPslldq(state.XmmLow[dstReg], state.XmmHigh[dstReg], count, true);
                        }
                    }
                    return 0;
                }

                default:
                    return 0;
            }
        }

        private ulong ExecuteAluRmR(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr, bool byte_op)
        {
            int aluOp = (opcode >> 3) & 7;
            if (byte_op)
            {
                byte src = (byte)state.GetGpr(inst.Reg);
                if (inst.Mod == 3)
                {
                    byte dst = (byte)state.GetGpr(inst.RM);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) SetRegByte(state, inst.RM, inst.HasRex, result);
                }
                else
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    byte dst = mem.ReadByte(addr);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) mem.WriteByte(addr, result);
                }
            }
            else
            {
                ulong src = state.GetGpr(inst.Reg);
                if (inst.Mod == 3)
                {
                    ulong dst = state.GetGpr(inst.RM);
                    if (inst.RexW)
                    {
                        ulong result = DoAlu64(aluOp, dst, src, state);
                        if (aluOp != 7) state.SetGpr(inst.RM, result);
                    }
                    else
                    {
                        uint result = DoAlu32(aluOp, (uint)dst, (uint)src, state);
                        if (aluOp != 7) state.SetGpr32(inst.RM, result);
                    }
                }
                else
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    if (inst.RexW)
                    {
                        ulong dst = mem.ReadUInt64(addr);
                        ulong result = DoAlu64(aluOp, dst, src, state);
                        if (aluOp != 7) mem.WriteUInt64(addr, result);
                    }
                    else
                    {
                        uint dst = mem.ReadUInt32(addr);
                        uint result = DoAlu32(aluOp, dst, (uint)src, state);
                        if (aluOp != 7) mem.WriteUInt32(addr, result);
                    }
                }
            }
            return 0;
        }

        private ulong ExecuteAluRRm(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr, bool byte_op)
        {
            int aluOp = (opcode >> 3) & 7;
            if (byte_op)
            {
                byte src;
                if (inst.Mod == 3) src = (byte)state.GetGpr(inst.RM);
                else src = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                byte dst = (byte)state.GetGpr(inst.Reg);
                byte result = DoAlu8(aluOp, dst, src, state);
                if (aluOp != 7) SetRegByte(state, inst.Reg, inst.HasRex, result);
            }
            else
            {
                ulong src;
                if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                if (inst.RexW)
                {
                    ulong result = DoAlu64(aluOp, state.GetGpr(inst.Reg), src, state);
                    if (aluOp != 7) state.SetGpr(inst.Reg, result);
                }
                else
                {
                    uint result = DoAlu32(aluOp, (uint)state.GetGpr(inst.Reg), (uint)src, state);
                    if (aluOp != 7) state.SetGpr32(inst.Reg, result);
                }
            }
            return 0;
        }

        private ulong ExecuteGroup1(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int aluOp = (inst.ModRM >> 3) & 7;

            if (opcode == 0x80)
            {
                byte src = (byte)inst.Immediate;
                if (inst.Mod == 3)
                {
                    byte dst = (byte)state.GetGpr(inst.RM);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) SetRegByte(state, inst.RM, inst.HasRex, result);
                }
                else
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    byte dst = mem.ReadByte(addr);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) mem.WriteByte(addr, result);
                }
                return 0;
            }

            // 81 and 83
            ulong immVal = (opcode == 0x83) ? (ulong)(long)(sbyte)(byte)inst.Immediate : (ulong)(long)(int)inst.Immediate;

            if (inst.Mod == 3)
            {
                if (inst.RexW)
                {
                    ulong dst = state.GetGpr(inst.RM);
                    ulong result = DoAlu64(aluOp, dst, immVal, state);
                    if (aluOp != 7) state.SetGpr(inst.RM, result);
                }
                else
                {
                    uint dst = (uint)state.GetGpr(inst.RM);
                    uint result = DoAlu32(aluOp, dst, (uint)immVal, state);
                    if (aluOp != 7) state.SetGpr32(inst.RM, result);
                }
            }
            else
            {
                ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                if (inst.RexW)
                {
                    ulong dst = mem.ReadUInt64(addr);
                    ulong result = DoAlu64(aluOp, dst, immVal, state);
                    if (aluOp != 7) mem.WriteUInt64(addr, result);
                }
                else
                {
                    uint dst = mem.ReadUInt32(addr);
                    uint result = DoAlu32(aluOp, dst, (uint)immVal, state);
                    if (aluOp != 7) mem.WriteUInt32(addr, result);
                }
            }
            return 0;
        }

        private ulong ExecuteShiftGroup(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int shiftOp = (inst.ModRM >> 3) & 7;
            int count;

            switch (opcode)
            {
                case 0xD0: case 0xD1: count = 1; break;
                case 0xD2: case 0xD3: count = (int)(state.RCX & 0x3F); break;
                default: count = (int)inst.Immediate & 0x3F; break;
            }

            bool isByte = (opcode == 0xC0 || opcode == 0xD0 || opcode == 0xD2);

            if (isByte)
            {
                byte val;
                if (inst.Mod == 3) val = (byte)state.GetGpr(inst.RM);
                else val = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                byte result = DoShift8(shiftOp, val, count, state);
                if (inst.Mod == 3) SetRegByte(state, inst.RM, inst.HasRex, result);
                else mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
            }
            else if (inst.RexW)
            {
                ulong val;
                if (inst.Mod == 3) val = state.GetGpr(inst.RM);
                else val = mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                ulong result = DoShift64(shiftOp, val, count, state);
                if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
            }
            else
            {
                uint val;
                if (inst.Mod == 3) val = (uint)state.GetGpr(inst.RM);
                else val = mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                uint result = DoShift32(shiftOp, val, count, state);
                if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
            }
            return 0;
        }

        private ulong ExecuteGroupFF(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int regOp = (inst.ModRM >> 3) & 7;
            ulong operand;
            bool force64Operand = regOp == 2 || regOp == 4 || regOp == 6;
            if (inst.Mod == 3)
                operand = state.GetGpr(inst.RM);
            else
                operand = (inst.RexW || force64Operand)
                    ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr))
                    : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));

            switch (regOp)
            {
                case 0: // INC
                    if (inst.RexW)
                    {
                        ulong result = operand + 1;
                        state.UpdateFlags64(result);
                        state.SetFlag(X86Flags.OF, operand == 0x7FFFFFFFFFFFFFFFUL);
                        if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                        else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    else
                    {
                        uint result = (uint)operand + 1;
                        state.UpdateFlags32(result);
                        state.SetFlag(X86Flags.OF, (uint)operand == 0x7FFFFFFFU);
                        if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                        else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    return 0;

                case 1: // DEC
                    if (inst.RexW)
                    {
                        ulong result = operand - 1;
                        state.UpdateFlags64(result);
                        state.SetFlag(X86Flags.OF, operand == 0x8000000000000000UL);
                        if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                        else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    else
                    {
                        uint result = (uint)operand - 1;
                        state.UpdateFlags32(result);
                        state.SetFlag(X86Flags.OF, (uint)operand == 0x80000000U);
                        if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                        else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    return 0;

                case 2: // CALL indirect
                    state.Push(mem, nextAddr);
                    state.RIP = operand;
                    return operand;

                case 4: // JMP indirect
                    state.RIP = operand;
                    return operand;

                case 6: // PUSH r/m
                    state.Push(mem, operand);
                    return 0;

                default:
                    return 0;
            }
        }

        private ulong ExecuteGroupF7(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int regOp = (inst.ModRM >> 3) & 7;
            ulong operand;
            if (inst.Mod == 3) operand = state.GetGpr(inst.RM);
            else operand = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));

            switch (regOp)
            {
                case 0: // TEST r/m, imm32
                {
                    ulong result = operand & (ulong)(long)(int)inst.Immediate;
                    if (inst.RexW) state.UpdateFlags64(result);
                    else state.UpdateFlags32((uint)result);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 2: // NOT
                {
                    ulong result = ~operand;
                    if (inst.Mod == 3)
                    {
                        if (inst.RexW) state.SetGpr(inst.RM, result);
                        else state.SetGpr32(inst.RM, (uint)result);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        if (inst.RexW) mem.WriteUInt64(addr, result);
                        else mem.WriteUInt32(addr, (uint)result);
                    }
                    return 0;
                }
                case 3: // NEG
                {
                    if (inst.RexW)
                    {
                        ulong result = (ulong)(-(long)operand);
                        state.UpdateFlags64(result);
                        state.SetFlag(X86Flags.CF, operand != 0);
                        if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                        else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    else
                    {
                        uint result = (uint)(-(int)(uint)operand);
                        state.UpdateFlags32(result);
                        state.SetFlag(X86Flags.CF, (uint)operand != 0);
                        if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                        else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    return 0;
                }
                case 4: // MUL (unsigned)
                    if (inst.RexW)
                    {
                        // 128-bit result: RDX:RAX = RAX * operand
                        UInt128Multiply(state.RAX, operand, out ulong lo, out ulong hi);
                        state.RAX = lo;
                        state.RDX = hi;
                        state.SetFlag(X86Flags.CF, hi != 0);
                        state.SetFlag(X86Flags.OF, hi != 0);
                    }
                    else
                    {
                        ulong result = (ulong)state.EAX * (uint)operand;
                        state.EAX = (uint)result;
                        state.EDX = (uint)(result >> 32);
                        state.SetFlag(X86Flags.CF, state.EDX != 0);
                        state.SetFlag(X86Flags.OF, state.EDX != 0);
                    }
                    return 0;
                case 5: // IMUL (signed, single-operand)
                    if (inst.RexW)
                    {
                        long sResult = (long)state.RAX * (long)operand;
                        state.RAX = (ulong)sResult;
                        state.RDX = (ulong)(sResult >> 63); // sign extension
                    }
                    else
                    {
                        long sResult = (int)state.EAX * (long)(int)(uint)operand;
                        state.EAX = (uint)sResult;
                        state.EDX = (uint)(sResult >> 32);
                    }
                    return 0;
                case 6: // DIV (unsigned)
                    if (inst.RexW)
                    {
                        if (operand == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        // For simplicity, handle the common case where RDX is 0
                        if (state.RDX == 0)
                        {
                            state.RAX = state.RAX / operand;
                            state.RDX = state.RAX % operand;
                        }
                        else
                        {
                            // Full 128÷64 division would go here; simplified for basic binaries
                            state.RAX = state.RAX / operand;
                            state.RDX = state.RAX % operand;
                        }
                    }
                    else
                    {
                        uint divisor = (uint)operand;
                        if (divisor == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        ulong dividend = ((ulong)state.EDX << 32) | state.EAX;
                        state.EAX = (uint)(dividend / divisor);
                        state.EDX = (uint)(dividend % divisor);
                    }
                    return 0;
                case 7: // IDIV (signed)
                    if (inst.RexW)
                    {
                        long divisor = (long)operand;
                        if (divisor == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        long dividend = (long)state.RAX;
                        state.RAX = (ulong)(dividend / divisor);
                        state.RDX = (ulong)(dividend % divisor);
                    }
                    else
                    {
                        int divisor = (int)(uint)operand;
                        if (divisor == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        long dividend = ((long)state.EDX << 32) | state.EAX;
                        state.EAX = (uint)(int)(dividend / divisor);
                        state.EDX = (uint)(int)(dividend % divisor);
                    }
                    return 0;
                default:
                    return 0;
            }
        }

        private ulong ExecuteGroupF6(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int regOp = (inst.ModRM >> 3) & 7;
            byte operand;
            if (inst.Mod == 3) operand = (byte)state.GetGpr(inst.RM);
            else operand = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));

            switch (regOp)
            {
                case 0: // TEST r/m8, imm8
                {
                    byte result = (byte)(operand & (byte)inst.Immediate);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 2: // NOT r/m8
                    if (inst.Mod == 3) SetRegByte(state, inst.RM, inst.HasRex, (byte)~operand);
                    else mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), (byte)~operand);
                    return 0;
                case 3: // NEG r/m8
                {
                    byte result = (byte)(-(sbyte)operand);
                    state.SetFlag(X86Flags.CF, operand != 0);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    if (inst.Mod == 3) SetRegByte(state, inst.RM, inst.HasRex, result);
                    else mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    return 0;
                }
                default:
                    return 0;
            }
        }

        // === Condition evaluation for Jcc/SETcc/CMOVcc ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool EvaluateCondition(int cc, CpuState state)
        {
            return cc switch
            {
                0x0 => state.GetFlag(X86Flags.OF),                                          // O
                0x1 => !state.GetFlag(X86Flags.OF),                                         // NO
                0x2 => state.GetFlag(X86Flags.CF),                                          // B/C/NAE
                0x3 => !state.GetFlag(X86Flags.CF),                                         // NB/NC/AE
                0x4 => state.GetFlag(X86Flags.ZF),                                          // E/Z
                0x5 => !state.GetFlag(X86Flags.ZF),                                         // NE/NZ
                0x6 => state.GetFlag(X86Flags.CF) || state.GetFlag(X86Flags.ZF),            // BE/NA
                0x7 => !state.GetFlag(X86Flags.CF) && !state.GetFlag(X86Flags.ZF),          // NBE/A
                0x8 => state.GetFlag(X86Flags.SF),                                          // S
                0x9 => !state.GetFlag(X86Flags.SF),                                         // NS
                0xA => state.GetFlag(X86Flags.PF),                                          // P/PE
                0xB => !state.GetFlag(X86Flags.PF),                                         // NP/PO
                0xC => state.GetFlag(X86Flags.SF) != state.GetFlag(X86Flags.OF),            // L/NGE
                0xD => state.GetFlag(X86Flags.SF) == state.GetFlag(X86Flags.OF),            // NL/GE
                0xE => state.GetFlag(X86Flags.ZF) || (state.GetFlag(X86Flags.SF) != state.GetFlag(X86Flags.OF)), // LE/NG
                0xF => !state.GetFlag(X86Flags.ZF) && (state.GetFlag(X86Flags.SF) == state.GetFlag(X86Flags.OF)), // NLE/G
                _ => false
            };
        }

        // === ALU operations ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong DoAlu64(int op, ulong a, ulong b, CpuState state)
        {
            ulong result;
            switch (op)
            {
                case 0: // ADD
                    result = a + b;
                    state.SetFlag(X86Flags.CF, result < a);
                    state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 1: // OR
                    result = a | b;
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    break;
                case 2: // ADC
                    ulong carry = state.GetFlag(X86Flags.CF) ? 1UL : 0;
                    result = a + b + carry;
                    state.SetFlag(X86Flags.CF, (carry != 0 && result <= a) || (carry == 0 && result < a));
                    state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 3: // SBB
                    ulong borrow = state.GetFlag(X86Flags.CF) ? 1UL : 0;
                    result = a - b - borrow;
                    state.SetFlag(X86Flags.CF, borrow != 0 ? a <= b : a < b);
                    state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 4: // AND
                    result = a & b;
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    break;
                case 5: // SUB
                    result = a - b;
                    state.SetFlag(X86Flags.CF, a < b);
                    state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 6: // XOR
                    result = a ^ b;
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    break;
                case 7: // CMP (same as SUB but result is discarded)
                    result = a - b;
                    state.SetFlag(X86Flags.CF, a < b);
                    state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x8000000000000000UL) != 0);
                    break;
                default:
                    result = 0;
                    break;
            }
            state.UpdateFlags64(result);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint DoAlu32(int op, uint a, uint b, CpuState state)
        {
            uint result;
            switch (op)
            {
                case 0: result = a + b; state.SetFlag(X86Flags.CF, result < a); state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x80000000U) != 0); break;
                case 1: result = a | b; state.SetFlag(X86Flags.CF, false); state.SetFlag(X86Flags.OF, false); break;
                case 2: { uint c = state.GetFlag(X86Flags.CF) ? 1U : 0; result = a + b + c; state.SetFlag(X86Flags.CF, (c != 0 && result <= a) || (c == 0 && result < a)); state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x80000000U) != 0); break; }
                case 3: { uint c = state.GetFlag(X86Flags.CF) ? 1U : 0; result = a - b - c; state.SetFlag(X86Flags.CF, c != 0 ? a <= b : a < b); state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x80000000U) != 0); break; }
                case 4: result = a & b; state.SetFlag(X86Flags.CF, false); state.SetFlag(X86Flags.OF, false); break;
                case 5: result = a - b; state.SetFlag(X86Flags.CF, a < b); state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x80000000U) != 0); break;
                case 6: result = a ^ b; state.SetFlag(X86Flags.CF, false); state.SetFlag(X86Flags.OF, false); break;
                case 7: result = a - b; state.SetFlag(X86Flags.CF, a < b); state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x80000000U) != 0); break;
                default: result = 0; break;
            }
            state.UpdateFlags32(result);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte DoAlu8(int op, byte a, byte b, CpuState state)
        {
            byte result;
            switch (op)
            {
                case 0: result = (byte)(a + b); state.SetFlag(X86Flags.CF, result < a); break;
                case 1: result = (byte)(a | b); state.SetFlag(X86Flags.CF, false); break;
                case 4: result = (byte)(a & b); state.SetFlag(X86Flags.CF, false); break;
                case 5: case 7: result = (byte)(a - b); state.SetFlag(X86Flags.CF, a < b); break;
                case 6: result = (byte)(a ^ b); state.SetFlag(X86Flags.CF, false); break;
                default: result = (byte)(a + b); break;
            }
            state.SetFlag(X86Flags.ZF, result == 0);
            state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
            return result;
        }

        // === Shift operations ===
        private static ulong DoShift64(int op, ulong val, int count, CpuState state)
        {
            if (count == 0) return val;
            count &= 63;
            ulong result = op switch
            {
                0 => val << count,               // ROL (simplified)
                1 => val >> count,               // ROR (simplified)
                4 => val << count,               // SHL/SAL
                5 => val >> count,               // SHR
                7 => (ulong)((long)val >> count), // SAR
                _ => val
            };
            if (op == 4 || op == 5 || op == 7)
                state.UpdateFlags64(result);
            return result;
        }

        private static uint DoShift32(int op, uint val, int count, CpuState state)
        {
            if (count == 0) return val;
            count &= 31;
            uint result = op switch
            {
                0 => (val << count) | (val >> (32 - count)),
                1 => (val >> count) | (val << (32 - count)),
                4 => val << count,
                5 => val >> count,
                7 => (uint)((int)val >> count),
                _ => val
            };
            if (op == 4 || op == 5 || op == 7)
                state.UpdateFlags32(result);
            return result;
        }

        private static byte DoShift8(int op, byte val, int count, CpuState state)
        {
            if (count == 0) return val;
            count &= 7;
            byte result = op switch
            {
                4 => (byte)(val << count),
                5 => (byte)(val >> count),
                7 => (byte)((sbyte)val >> count),
                _ => val
            };
            state.SetFlag(X86Flags.ZF, result == 0);
            state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
            return result;
        }

        // === CPUID ===
        private static void ExecuteCpuid(CpuState state)
        {
            uint leaf = state.EAX;
            switch (leaf)
            {
                case 0: // Vendor string
                    state.EAX = 1; // Max leaf
                    state.EBX = 0x756E694C; // "Linu"
                    state.EDX = 0x72547878; // "xxTr"
                    state.ECX = 0x006C6E61; // "anl\0"
                    break;
                case 1: // Feature info
                    state.EAX = 0x000306C3; // Family/model (Haswell-like)
                    state.EBX = 0x00010800; // CLFLUSH size=8, logical CPUs=1
                    // ECX: SSE3(0), PCLMULQDQ(1), SSSE3(9), SSE4.1(19), SSE4.2(20), POPCNT(23)
                    state.ECX = (1 << 0) | (1 << 9) | (1 << 19) | (1 << 20) | (1 << 23);
                    // EDX: FPU(0), TSC(4), MSR(5), CX8(8), CMOV(15), MMX(23), FXSR(24), SSE(25), SSE2(26)
                    state.EDX = (1 << 0) | (1 << 4) | (1 << 5) | (1 << 8) |
                                (1 << 15) | (1 << 23) | (1 << 24) | (1 << 25) | (1 << 26);
                    break;
                default:
                    state.EAX = state.EBX = state.ECX = state.EDX = 0;
                    break;
            }
        }

        // === Effective address computation ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ComputeEffectiveAddress(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int mod = inst.Mod;
            int rm = inst.RM;

            if (mod == 3)
                return state.GetGpr(rm); // Register direct — shouldn't be called for memory ops

            ulong addr;

            if (!inst.HasSIB)
            {
                if (mod == 0 && (rm & 7) == 5)
                {
                    // RIP-relative addressing
                    addr = nextAddr + (ulong)(long)inst.Displacement;
                }
                else
                {
                    addr = state.GetGpr(rm);
                    if (mod == 1 || mod == 2)
                        addr += (ulong)(long)inst.Displacement;
                }
            }
            else
            {
                // SIB addressing
                int baseReg = inst.SIB & 7;
                int indexReg = (inst.SIB >> 3) & 7;
                int scale = (inst.SIB >> 6) & 3;

                if (inst.RexB) baseReg |= 8;
                if (inst.RexX) indexReg |= 8;

                if (mod == 0 && (baseReg & 7) == 5)
                    addr = (ulong)(long)inst.Displacement; // disp32 only
                else
                    addr = state.GetGpr(baseReg);

                if (indexReg != 4) // RSP cannot be index
                    addr += state.GetGpr(indexReg) << scale;

                if (mod == 1 || mod == 2)
                    addr += (ulong)(long)inst.Displacement;
            }

            return ApplySegmentBase(inst, state, addr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ApplySegmentBase(DecodedInstruction inst, CpuState state, ulong address)
        {
            return inst.SegmentOverridePrefix switch
            {
                0x64 => state.FSBase + address, // FS
                0x65 => state.GSBase + address, // GS
                _ => address,
            };
        }

        private static string FormatBytes(byte[] bytes)
        {
            if (bytes.Length == 0)
                return "<empty>";

            var sb = new StringBuilder(bytes.Length * 3 - 1);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i != 0)
                    sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string FormatOpcode(byte[] opcode)
        {
            if (opcode.Length == 0)
                return "<none>";

            var sb = new StringBuilder(opcode.Length * 2);
            for (int i = 0; i < opcode.Length; i++)
                sb.Append(opcode[i].ToString("X2"));
            return sb.ToString();
        }

        // === Helper: set byte register ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetRegByte(CpuState state, int reg, bool hasRex, byte value)
        {
            if (hasRex || reg < 4)
            {
                // With REX or regs 0-3: low byte of GPR
                ulong current = state.GetGpr(reg);
                state.SetGpr(reg, (current & 0xFFFFFFFFFFFFFF00UL) | value);
            }
            else
            {
                // Without REX, regs 4-7 map to AH/CH/DH/BH
                int hiReg = reg - 4; // 0=AH(RAX), 1=CH(RCX), 2=DH(RDX), 3=BH(RBX)
                ulong current = state.GetGpr(hiReg);
                state.SetGpr(hiReg, (current & 0xFFFFFFFFFFFF00FFUL) | ((ulong)value << 8));
            }
        }

        // === Float/double ↔ int bit-cast helpers (unsafe for .NET Native UWP compatibility) ===
        private static unsafe float UInt32ToFloat(uint v) { return *(float*)&v; }
        private static unsafe uint FloatToUInt32(float v) { return *(uint*)&v; }
        private static unsafe double UInt64ToDouble(ulong v) { return *(double*)&v; }
        private static unsafe ulong DoubleToUInt64(double v) { return *(ulong*)&v; }

        // === 128-bit multiply helper ===
        private static void UInt128Multiply(ulong a, ulong b, out ulong lo, out ulong hi)        {
            // Split into 32-bit parts to avoid overflow
            ulong aLo = (uint)a, aHi = a >> 32;
            ulong bLo = (uint)b, bHi = b >> 32;

            ulong p0 = aLo * bLo;
            ulong p1 = aLo * bHi;
            ulong p2 = aHi * bLo;
            ulong p3 = aHi * bHi;

            ulong mid = (p0 >> 32) + (uint)p1 + (uint)p2;
            lo = (uint)p0 | (mid << 32);
            hi = p3 + (p1 >> 32) + (p2 >> 32) + (mid >> 32);
        }

        // === SSE helper methods ===

        private static ulong ArithPackedFloat32Pair(byte op, uint a0, uint a1, uint b0, uint b1)
        {
            float r0 = ArithF32(op, UInt32ToFloat(a0), UInt32ToFloat(b0));
            float r1 = ArithF32(op, UInt32ToFloat(a1), UInt32ToFloat(b1));
            return (uint)FloatToUInt32(r0) | ((ulong)(uint)FloatToUInt32(r1) << 32);
        }

        private static float ArithF32(byte op, float a, float b) => op switch
        {
            0x58 => a + b, 0x59 => a * b, 0x5C => a - b,
            0x5E => b != 0 ? a / b : float.NaN,
            0x5D => (float)Math.Min(a, b), 0x5F => (float)Math.Max(a, b),
            _ => a
        };

        private static ulong SqrtPackedFloat32Pair(uint a0, uint a1)
        {
            float r0 = (float)Math.Sqrt(UInt32ToFloat(a0));
            float r1 = (float)Math.Sqrt(UInt32ToFloat(a1));
            return (uint)FloatToUInt32(r0) | ((ulong)(uint)FloatToUInt32(r1) << 32);
        }

        private static ulong PcmpeqB(ulong a, ulong b)
        {
            ulong r = 0;
            for (int i = 0; i < 8; i++)
            {
                byte av = (byte)(a >> (i * 8)), bv = (byte)(b >> (i * 8));
                if (av == bv) r |= (0xFFUL << (i * 8));
            }
            return r;
        }

        private static ulong PcmpeqW(ulong a, ulong b)
        {
            ulong r = 0;
            for (int i = 0; i < 4; i++)
            {
                ushort av = (ushort)(a >> (i * 16)), bv = (ushort)(b >> (i * 16));
                if (av == bv) r |= (0xFFFFUL << (i * 16));
            }
            return r;
        }

        private static ulong PcmpeqD(ulong a, ulong b)
        {
            ulong r = 0;
            if ((uint)a == (uint)b) r |= 0xFFFFFFFFUL;
            if ((uint)(a >> 32) == (uint)(b >> 32)) r |= 0xFFFFFFFF00000000UL;
            return r;
        }

        private static ulong AddBytes(ulong a, ulong b)
        {
            ulong r = 0;
            for (int i = 0; i < 8; i++)
                r |= ((ulong)(byte)((byte)(a >> (i * 8)) + (byte)(b >> (i * 8)))) << (i * 8);
            return r;
        }

        private static ulong SubBytes(ulong a, ulong b)
        {
            ulong r = 0;
            for (int i = 0; i < 8; i++)
                r |= ((ulong)(byte)((byte)(a >> (i * 8)) - (byte)(b >> (i * 8)))) << (i * 8);
            return r;
        }

        private static ulong AddWords(ulong a, ulong b)
        {
            ulong r = 0;
            for (int i = 0; i < 4; i++)
                r |= ((ulong)(ushort)((ushort)(a >> (i * 16)) + (ushort)(b >> (i * 16)))) << (i * 16);
            return r;
        }

        private static ulong SubWords(ulong a, ulong b)
        {
            ulong r = 0;
            for (int i = 0; i < 4; i++)
                r |= ((ulong)(ushort)((ushort)(a >> (i * 16)) - (ushort)(b >> (i * 16)))) << (i * 16);
            return r;
        }

        private static ulong AddDwords(ulong a, ulong b)
            => ((ulong)((uint)(a) + (uint)(b))) | (((ulong)((uint)(a >> 32) + (uint)(b >> 32))) << 32);

        private static ulong SubDwords(ulong a, ulong b)
            => ((ulong)((uint)(a) - (uint)(b))) | (((ulong)((uint)(a >> 32) - (uint)(b >> 32))) << 32);

        private static ulong PminubQword(ulong a, ulong b)
        {
            ulong r = 0;
            for (int i = 0; i < 8; i++)
            {
                byte av = (byte)(a >> (i * 8)), bv = (byte)(b >> (i * 8));
                r |= ((ulong)Math.Min(av, bv)) << (i * 8);
            }
            return r;
        }

        private static (ulong lo, ulong hi) PunpcklBW(ulong aLow, ulong bLow)
        {
            // Interleave low bytes of a and b: a0,b0, a1,b1, a2,b2, a3,b3 (lo), a4,b4,...(hi)
            ulong lo = 0, hi = 0;
            for (int i = 0; i < 4; i++)
            {
                lo |= ((ulong)(byte)(aLow >> (i * 8))) << (i * 16);
                lo |= ((ulong)(byte)(bLow >> (i * 8))) << (i * 16 + 8);
            }
            for (int i = 4; i < 8; i++)
            {
                hi |= ((ulong)(byte)(aLow >> (i * 8))) << ((i - 4) * 16);
                hi |= ((ulong)(byte)(bLow >> (i * 8))) << ((i - 4) * 16 + 8);
            }
            return (lo, hi);
        }

        private static (ulong lo, ulong hi) PunpckhBW(ulong aHigh, ulong bHigh)
            => PunpcklBW(aHigh, bHigh); // Unpack from high half uses same logic

        private static (ulong lo, ulong hi) PunpcklWD(ulong aLow, ulong bLow)
        {
            ulong lo = 0, hi = 0;
            for (int i = 0; i < 2; i++)
            {
                lo |= ((ulong)(ushort)(aLow >> (i * 16))) << (i * 32);
                lo |= ((ulong)(ushort)(bLow >> (i * 16))) << (i * 32 + 16);
            }
            for (int i = 2; i < 4; i++)
            {
                hi |= ((ulong)(ushort)(aLow >> (i * 16))) << ((i - 2) * 32);
                hi |= ((ulong)(ushort)(bLow >> (i * 16))) << ((i - 2) * 32 + 16);
            }
            return (lo, hi);
        }

        private static (ulong lo, ulong hi) PunpckhWD(ulong aHigh, ulong bHigh)
            => PunpcklWD(aHigh, bHigh);

        private static (ulong lo, ulong hi) PunpcklDQ(ulong aLow, ulong bLow)
            => ((uint)aLow | ((ulong)(uint)bLow << 32), (uint)(aLow >> 32) | ((ulong)(uint)(bLow >> 32) << 32));

        private static (ulong lo, ulong hi) PunpckhDQ(ulong aHigh, ulong bHigh)
            => PunpcklDQ(aHigh, bHigh);

        private static ulong ShiftWords(ulong val, int count, bool left, bool arithmetic)
        {
            if (count >= 16) return 0;
            ulong r = 0;
            for (int i = 0; i < 4; i++)
            {
                short w = (short)(ushort)(val >> (i * 16));
                int shifted = left ? (w << count) : (arithmetic ? (w >> count) : ((ushort)w >> count));
                r |= ((ulong)(ushort)shifted) << (i * 16);
            }
            return r;
        }

        private static ulong ShiftDwords(ulong val, int count, bool left, bool arithmetic)
        {
            if (count >= 32) return 0;
            uint lo = (uint)val, hi = (uint)(val >> 32);
            if (left)
                return ((ulong)(lo << count)) | ((ulong)(hi << count) << 32);
            if (arithmetic)
                return ((ulong)(uint)((int)lo >> count)) | ((ulong)(uint)((int)hi >> count) << 32);
            return ((ulong)(lo >> count)) | ((ulong)(hi >> count) << 32);
        }

        private static (ulong lo, ulong hi) PsrldqPslldq(ulong lo, ulong hi, int byteCount, bool left)
        {
            // Shift entire 128-bit register by byteCount bytes
            if (byteCount >= 16) return (0, 0);
            if (byteCount == 0) return (lo, hi);
            int bits = byteCount * 8;
            if (left) // PSLLDQ — shift towards higher addresses
            {
                if (byteCount < 8)
                    return ((lo << bits), (hi << bits) | (lo >> (64 - bits)));
                else
                    return (0, lo << (bits - 64));
            }
            else // PSRLDQ — shift towards lower addresses
            {
                if (byteCount < 8)
                    return ((lo >> bits) | (hi << (64 - bits)), hi >> bits);
                else
                    return (hi >> (bits - 64), 0);
            }
        }
    }
}
