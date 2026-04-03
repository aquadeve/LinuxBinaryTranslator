// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// x86_64 instruction decoder for the native translation pipeline.
// Decodes x86_64 machine code from the ELF binary into DecodedInstruction
// objects that the BlockTranslator converts into C# delegates.

using System;
using LinuxBinaryTranslator.Cpu.Translation;
using LinuxBinaryTranslator.Memory;

namespace LinuxBinaryTranslator.Cpu
{
    /// <summary>
    /// Decodes x86_64 instructions from virtual memory into structured
    /// DecodedInstruction objects. Supports the subset of the x86_64 ISA
    /// needed for basic statically-linked Linux CLI binaries.
    /// </summary>
    public sealed class InstructionDecoder
    {
        private readonly VirtualMemoryManager _memory;

        public InstructionDecoder(VirtualMemoryManager memory)
        {
            _memory = memory;
        }

        /// <summary>
        /// Decode a single x86_64 instruction starting at the given address.
        /// </summary>
        public DecodedInstruction Decode(ulong address)
        {
            var inst = new DecodedInstruction { Address = address };
            ulong pos = address;

            // === Legacy prefixes ===
            bool hasOperandOverride = false;
            bool hasAddressOverride = false;
            bool hasRepPrefix = false;
            bool hasRepnePrefix = false;
            byte segmentOverridePrefix = 0;

            while (true)
            {
                byte b = _memory.ReadByte(pos);
                switch (b)
                {
                    case 0x66: hasOperandOverride = true; pos++; continue;
                    case 0x67: hasAddressOverride = true; pos++; continue;
                    case 0xF0: pos++; continue; // LOCK prefix (ignored for translation)
                    case 0xF2: hasRepnePrefix = true; pos++; continue;
                    case 0xF3: hasRepPrefix = true; pos++; continue;
                    case 0x2E: case 0x3E: case 0x26: case 0x64:
                    case 0x65: case 0x36:
                        segmentOverridePrefix = b;
                        pos++;
                        continue; // Segment override prefixes
                }
                break;
            }

            // === REX prefix (0x40-0x4F) ===
            byte current = _memory.ReadByte(pos);
            if (current >= 0x40 && current <= 0x4F)
            {
                inst.HasRex = true;
                inst.RexW = (current & 0x08) != 0;
                inst.RexR = (current & 0x04) != 0;
                inst.RexX = (current & 0x02) != 0;
                inst.RexB = (current & 0x01) != 0;
                pos++;
                current = _memory.ReadByte(pos);
            }

            // Make prefix state available to opcode-specific decode logic.
            inst.HasRepPrefix = hasRepPrefix;
            inst.HasRepnePrefix = hasRepnePrefix;
            inst.HasOperandOverride = hasOperandOverride;
            inst.HasAddressOverride = hasAddressOverride;
            inst.SegmentOverridePrefix = segmentOverridePrefix;

            // === Opcode ===
            if (current == 0x0F)
            {
                // Two-byte opcode
                byte second = _memory.ReadByte(pos + 1);
                inst.Opcode = new byte[] { current, second };
                pos += 2;
                DecodeTwoByteOpcode(inst, second, ref pos);
            }
            else
            {
                inst.Opcode = new byte[] { current };
                pos++;
                DecodeOneByteOpcode(inst, current, ref pos, hasOperandOverride);
            }

            inst.Length = (int)(pos - address);
            return inst;
        }

        private void DecodeOneByteOpcode(DecodedInstruction inst, byte opcode, ref ulong pos, bool operandOverride)
        {
            switch (opcode)
            {
                // NOP
                case 0x90:
                    break;

                // RET
                case 0xC3:
                    inst.IsTerminator = true;
                    break;

                // RET imm16
                case 0xC2:
                    inst.Immediate = _memory.ReadUInt16(pos);
                    inst.ImmediateSize = 2;
                    pos += 2;
                    inst.IsTerminator = true;
                    break;

                // PUSH r64 (50+rd)
                case 0x50: case 0x51: case 0x52: case 0x53:
                case 0x54: case 0x55: case 0x56: case 0x57:
                    break;

                // POP r64 (58+rd)
                case 0x58: case 0x59: case 0x5A: case 0x5B:
                case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                    break;

                // MOV r/m, r (88/89)
                case 0x88: case 0x89:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOV r, r/m (8A/8B)
                case 0x8A: case 0x8B:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOV r/m, imm (C6/C7)
                case 0xC6:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;
                case 0xC7:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    break;

                // MOV r64, imm64 (B8+rd with REX.W) / MOV r32, imm32 (B8+rd)
                case 0xB8: case 0xB9: case 0xBA: case 0xBB:
                case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                    if (inst.RexW)
                    {
                        inst.Immediate = (long)_memory.ReadUInt64(pos);
                        inst.ImmediateSize = 8;
                        pos += 8;
                    }
                    else
                    {
                        inst.Immediate = (int)_memory.ReadUInt32(pos);
                        inst.ImmediateSize = 4;
                        pos += 4;
                    }
                    break;

                // MOV r8, imm8 (B0+rb)
                case 0xB0: case 0xB1: case 0xB2: case 0xB3:
                case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // ADD/OR/ADC/SBB/AND/SUB/XOR/CMP r/m, imm8 (80)
                case 0x80:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = (sbyte)_memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // ADD/OR/ADC/SBB/AND/SUB/XOR/CMP r/m, imm32 (81)
                case 0x81:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    break;

                // ADD/OR/ADC/SBB/AND/SUB/XOR/CMP r/m, imm8 sign-extended (83)
                case 0x83:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = (sbyte)_memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // ADD r/m, r (01) / ADD r, r/m (03)
                case 0x00: case 0x01: case 0x02: case 0x03:
                // ADC r/m, r (11) / ADC r, r/m (13)
                case 0x10: case 0x11: case 0x12: case 0x13:
                // SBB r/m, r (19) / SBB r, r/m (1B)
                case 0x18: case 0x19: case 0x1A: case 0x1B:
                // OR r/m, r (09) / OR r, r/m (0B)
                case 0x08: case 0x09: case 0x0A: case 0x0B:
                // AND r/m, r (21) / AND r, r/m (23)
                case 0x20: case 0x21: case 0x22: case 0x23:
                // SUB r/m, r (29) / SUB r, r/m (2B)
                case 0x28: case 0x29: case 0x2A: case 0x2B:
                // XOR r/m, r (31) / XOR r, r/m (33)
                case 0x30: case 0x31: case 0x32: case 0x33:
                // CMP r/m, r (39) / CMP r, r/m (3B)
                case 0x38: case 0x39: case 0x3A: case 0x3B:
                    DecodeModRM(inst, ref pos);
                    break;

                // ADD/OR/AND/SUB/XOR/CMP AL/RAX, imm
                case 0x04: case 0x0C: case 0x14: case 0x1C:
                case 0x24: case 0x2C: case 0x34: case 0x3C:
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;
                case 0x05: case 0x0D: case 0x15: case 0x1D:
                case 0x25: case 0x2D: case 0x35: case 0x3D:
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    break;

                // TEST r/m, r (84/85)
                case 0x84: case 0x85:
                    DecodeModRM(inst, ref pos);
                    break;

                // TEST AL/RAX, imm (A8/A9)
                case 0xA8:
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;
                case 0xA9:
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    break;

                // XCHG r, r/m (86/87)
                case 0x86: case 0x87:
                    DecodeModRM(inst, ref pos);
                    break;

                // LEA r, m (8D)
                case 0x8D:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOV r/m, Sreg (8C) / MOV Sreg, r/m (8E) — rare in 64-bit
                case 0x8C: case 0x8E:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOVSB/MOVSQ (A4/A5)
                case 0xA4: case 0xA5:
                    break;

                // STOSB/STOSQ (AA/AB)
                case 0xAA: case 0xAB:
                    break;

                // LODSB/LODSQ (AC/AD)
                case 0xAC: case 0xAD:
                    break;

                // SCASB/SCASQ (AE/AF)
                case 0xAE: case 0xAF:
                    break;

                // Shift/rotate group (C0/C1 with imm8, D0/D1 by 1, D2/D3 by CL)
                case 0xC0:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;
                case 0xC1:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;
                case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                    DecodeModRM(inst, ref pos);
                    break;

                // INC/DEC/CALL/JMP/PUSH group (FF)
                case 0xFF:
                    DecodeModRM(inst, ref pos);
                    int ffReg = (inst.ModRM >> 3) & 7;
                    if (ffReg == 2 || ffReg == 4) // CALL/JMP indirect
                        inst.IsTerminator = true;
                    break;

                // NOT/NEG/MUL/IMUL/DIV/IDIV (F6/F7)
                case 0xF6:
                    DecodeModRM(inst, ref pos);
                    if (((inst.ModRM >> 3) & 7) == 0) // TEST r/m8, imm8
                    {
                        inst.Immediate = _memory.ReadByte(pos);
                        inst.ImmediateSize = 1;
                        pos += 1;
                    }
                    break;
                case 0xF7:
                    DecodeModRM(inst, ref pos);
                    if (((inst.ModRM >> 3) & 7) == 0) // TEST r/m, imm32
                    {
                        inst.Immediate = (int)_memory.ReadUInt32(pos);
                        inst.ImmediateSize = 4;
                        pos += 4;
                    }
                    break;

                // IMUL r, r/m, imm (69/6B)
                case 0x69:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    break;
                case 0x6B:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = (sbyte)_memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // JMP rel8 (EB)
                case 0xEB:
                    inst.Immediate = (sbyte)_memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    inst.IsTerminator = true;
                    break;

                // JMP rel32 (E9)
                case 0xE9:
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    inst.IsTerminator = true;
                    break;

                // CALL rel32 (E8)
                case 0xE8:
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    inst.IsTerminator = true;
                    break;

                // Jcc rel8 (70-7F)
                case 0x70: case 0x71: case 0x72: case 0x73:
                case 0x74: case 0x75: case 0x76: case 0x77:
                case 0x78: case 0x79: case 0x7A: case 0x7B:
                case 0x7C: case 0x7D: case 0x7E: case 0x7F:
                    inst.Immediate = (sbyte)_memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    inst.IsTerminator = true;
                    break;

                // SYSCALL (0F 05 is handled in two-byte; but Linux also has INT 0x80)
                case 0xCD: // INT imm8
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    if (inst.Immediate == 0x80)
                        inst.IsSyscall = true;
                    inst.IsTerminator = true;
                    break;

                // LEAVE (C9)
                case 0xC9:
                    break;

                // CLC/STC/CMC/CLD/STD
                case 0xF8: case 0xF9: case 0xF5: case 0xFC: case 0xFD:
                    break;

                // CBW/CWDE/CDQE (98)
                case 0x98:
                    break;

                // CWD/CDQ/CQO (99)
                case 0x99:
                    break;

                // PUSH imm8 (6A) / PUSH imm32 (68)
                case 0x6A:
                    inst.Immediate = (sbyte)_memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;
                case 0x68:
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    break;

                // MOVSX/MOVZX are two-byte (0F BE/BF/B6/B7) — handled below

                // MOVSXD r64, r/m32 (0x63 with REX.W) — critical for sign-extending
                // 32-bit values to 64-bit, very common in static binaries
                case 0x63:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOV moffs (A0-A3): direct memory address encoding
                case 0xA0: // MOV AL, moffs8
                    if (inst.RexW || !operandOverride)
                    {
                        inst.Immediate = (long)_memory.ReadUInt64(pos);
                        inst.ImmediateSize = 8;
                        pos += 8;
                    }
                    else
                    {
                        inst.Immediate = (int)_memory.ReadUInt32(pos);
                        inst.ImmediateSize = 4;
                        pos += 4;
                    }
                    break;
                case 0xA1: // MOV RAX/EAX, moffs
                    if (inst.RexW || !operandOverride)
                    {
                        inst.Immediate = (long)_memory.ReadUInt64(pos);
                        inst.ImmediateSize = 8;
                        pos += 8;
                    }
                    else
                    {
                        inst.Immediate = (int)_memory.ReadUInt32(pos);
                        inst.ImmediateSize = 4;
                        pos += 4;
                    }
                    break;
                case 0xA2: // MOV moffs8, AL
                    if (inst.RexW || !operandOverride)
                    {
                        inst.Immediate = (long)_memory.ReadUInt64(pos);
                        inst.ImmediateSize = 8;
                        pos += 8;
                    }
                    else
                    {
                        inst.Immediate = (int)_memory.ReadUInt32(pos);
                        inst.ImmediateSize = 4;
                        pos += 4;
                    }
                    break;
                case 0xA3: // MOV moffs, RAX/EAX
                    if (inst.RexW || !operandOverride)
                    {
                        inst.Immediate = (long)_memory.ReadUInt64(pos);
                        inst.ImmediateSize = 8;
                        pos += 8;
                    }
                    else
                    {
                        inst.Immediate = (int)_memory.ReadUInt32(pos);
                        inst.ImmediateSize = 4;
                        pos += 4;
                    }
                    break;

                // CMPSB/CMPSQ (A6/A7) — compare string bytes/qwords
                case 0xA6: case 0xA7:
                    break;

                // BT/BTS/BTR/BTC group (0F BA with ModRM) — bit test with immediate
                // Handled in two-byte section

                // ENTER (C8) — create stack frame
                case 0xC8:
                    inst.Immediate = _memory.ReadUInt16(pos);
                    inst.ImmediateSize = 2;
                    pos += 2;
                    // Nesting level byte
                    pos += 1;
                    break;

                // HLT (F4) - should not appear in user code
                case 0xF4:
                    inst.IsTerminator = true;
                    break;

                // PUSHFQ (9C) / POPFQ (9D)
                case 0x9C: case 0x9D:
                    break;

                // SAHF (9E) / LAHF (9F)
                case 0x9E: case 0x9F:
                    break;

                // XCHG r64, RAX (91-97) — also XCHG RAX, RAX = NOP (90)
                case 0x91: case 0x92: case 0x93:
                case 0x94: case 0x95: case 0x96: case 0x97:
                    break;

                // LOOP (E2), LOOPE/LOOPZ (E1), LOOPNE/LOOPNZ (E0), JRCXZ (E3)
                case 0xE0: case 0xE1: case 0xE2: case 0xE3:
                    inst.Immediate = (sbyte)_memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    inst.IsTerminator = true;
                    break;

                // x87 FPU (D8-DF) — decode ModRM, execute as no-op stubs
                case 0xD8: case 0xD9: case 0xDA: case 0xDB:
                case 0xDC: case 0xDD: case 0xDE: case 0xDF:
                    DecodeModRM(inst, ref pos);
                    break;

                // UD2 (0F 0B) - illegal instruction
                case 0x0F:
                    // Already handled as two-byte prefix above
                    break;

                default:
                    // Unknown opcode - treat as 1-byte NOP for resilience
                    break;
            }
        }

        private void DecodeTwoByteOpcode(DecodedInstruction inst, byte second, ref ulong pos)
        {
            switch (second)
            {
                // SYSCALL (0F 05)
                case 0x05:
                    inst.IsSyscall = true;
                    inst.IsTerminator = true;
                    break;

                // Jcc rel32 (0F 80-8F)
                case 0x80: case 0x81: case 0x82: case 0x83:
                case 0x84: case 0x85: case 0x86: case 0x87:
                case 0x88: case 0x89: case 0x8A: case 0x8B:
                case 0x8C: case 0x8D: case 0x8E: case 0x8F:
                    inst.Immediate = (int)_memory.ReadUInt32(pos);
                    inst.ImmediateSize = 4;
                    pos += 4;
                    inst.IsTerminator = true;
                    break;

                // SETcc (0F 90-9F)
                case 0x90: case 0x91: case 0x92: case 0x93:
                case 0x94: case 0x95: case 0x96: case 0x97:
                case 0x98: case 0x99: case 0x9A: case 0x9B:
                case 0x9C: case 0x9D: case 0x9E: case 0x9F:
                    DecodeModRM(inst, ref pos);
                    break;

                // CMOVcc (0F 40-4F)
                case 0x40: case 0x41: case 0x42: case 0x43:
                case 0x44: case 0x45: case 0x46: case 0x47:
                case 0x48: case 0x49: case 0x4A: case 0x4B:
                case 0x4C: case 0x4D: case 0x4E: case 0x4F:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOVZX r, r/m8 (0F B6) / MOVZX r, r/m16 (0F B7)
                case 0xB6: case 0xB7:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOVSX r, r/m8 (0F BE) / MOVSX r, r/m16 (0F BF)
                case 0xBE: case 0xBF:
                    DecodeModRM(inst, ref pos);
                    break;

                // IMUL r, r/m (0F AF)
                case 0xAF:
                    DecodeModRM(inst, ref pos);
                    break;

                // BSF/BSR (0F BC/BD)
                case 0xBC: case 0xBD:
                    DecodeModRM(inst, ref pos);
                    break;

                // BT/BTS/BTR/BTC (0F A3/AB/B3/BB)
                case 0xA3: case 0xAB: case 0xB3: case 0xBB:
                    DecodeModRM(inst, ref pos);
                    break;

                // XADD (0F C0/C1)
                case 0xC0: case 0xC1:
                    DecodeModRM(inst, ref pos);
                    break;

                // CMPXCHG (0F B0/B1)
                case 0xB0: case 0xB1:
                    DecodeModRM(inst, ref pos);
                    break;

                // ENDBR64 / ENDBR32 (F3 0F 1E FA / F3 0F 1E FB)
                // Decode the ModRM byte so instruction length stays correct.
                case 0x1E:
                    DecodeModRM(inst, ref pos);
                    break;

                // NOP (0F 1F /0) - multi-byte NOP
                case 0x1F:
                    DecodeModRM(inst, ref pos);
                    break;

                // UD2 (0F 0B)
                case 0x0B:
                    inst.IsTerminator = true;
                    break;

                // CPUID (0F A2) - we can emulate this
                case 0xA2:
                    break;

                // RDTSC (0F 31)
                case 0x31:
                    break;

                // BT/BTS/BTR/BTC r/m, imm8 (0F BA /4-/7)
                case 0xBA:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // BSWAP (0F C8+rd)
                case 0xC8: case 0xC9: case 0xCA: case 0xCB:
                case 0xCC: case 0xCD: case 0xCE: case 0xCF:
                    break;

                // MOVSX r, r/m32 (0F 63 — MOVSXD in 64-bit mode handled as one-byte 0x63)
                // POPCNT (F3 0F B8) — population count
                case 0xB8:
                    if (inst.HasRepPrefix)
                    {
                        DecodeModRM(inst, ref pos);
                    }
                    else
                    {
                        inst.Immediate = (int)_memory.ReadUInt32(pos);
                        inst.ImmediateSize = 4;
                        pos += 4;
                    }
                    break;

                // LZCNT/TZCNT (F3 0F BD / F3 0F BC) — leading/trailing zero count
                // Already handled as BSF/BSR above

                // === SSE/SSE2 instructions commonly found in static binaries ===
                // These are decoded to prevent choking on SSE instructions used for
                // memory initialization (xorps, movaps, movdqa) even in integer code.

                // MOVAPS/MOVUPS xmm, xmm/m128 (0F 28/29, 0F 10/11)
                case 0x10: case 0x11: case 0x28: case 0x29:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOVDQA/MOVDQU xmm, xmm/m128 (66 0F 6F/7F)
                case 0x6F: case 0x7F:
                    DecodeModRM(inst, ref pos);
                    break;

                // XORPS/XORPD xmm, xmm/m128 (0F 57), PXOR (66 0F EF)
                case 0x57: case 0xEF:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOVD/MOVQ xmm, r/m32/64 (66 0F 6E/7E)
                case 0x6E: case 0x7E:
                    DecodeModRM(inst, ref pos);
                    break;

                // MOVSS (F3 0F 10/11), MOVSD (F2 0F 10/11) — already covered by 0x10/0x11

                // PUNPCKL/H (66 0F 60-6D), PACK/UNPACK
                case 0x60: case 0x61: case 0x62: case 0x63:
                case 0x64: case 0x65: case 0x66: case 0x67:
                case 0x68: case 0x69: case 0x6A: case 0x6B:
                case 0x6C: case 0x6D:
                    DecodeModRM(inst, ref pos);
                    break;

                // PCMPEQ/PCMPGT (66 0F 74-76)
                case 0x74: case 0x75: case 0x76:
                    DecodeModRM(inst, ref pos);
                    break;

                // PSHUFD (66 0F 70), PSHUFHW/PSHUFLW (F3/F2 0F 70)
                case 0x70:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // MOVQ xmm, xmm/m64 (F3 0F 7E) — already covered by 0x7E
                // MOVHPS/MOVLPS (0F 12/13/16/17)
                case 0x12: case 0x13: case 0x16: case 0x17:
                    DecodeModRM(inst, ref pos);
                    break;

                // ADDPS/SUBPS/MULPS/DIVPS etc. (0F 58-5F)
                case 0x58: case 0x59: case 0x5A: case 0x5B:
                case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                    DecodeModRM(inst, ref pos);
                    break;

                // CMPPS/CMPPD (0F C2) with imm8
                case 0xC2:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // SHUFPS/SHUFPD (0F C6) with imm8
                case 0xC6:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // PAND/POR/PANDN/PXOR (0F DB/EB/DF/EF)
                case 0xDB: case 0xEB: case 0xDF:
                    DecodeModRM(inst, ref pos);
                    break;

                // PADDB-PADDQ, PSUBB-PSUBQ (0F FC-FE, 0F D4, 0F F8-FA)
                case 0xD4: case 0xF8: case 0xF9: case 0xFA:
                case 0xFC: case 0xFD: case 0xFE:
                    DecodeModRM(inst, ref pos);
                    break;

                // PMULLW/PMULHW (0F D5/E5)
                case 0xD5: case 0xE5:
                    DecodeModRM(inst, ref pos);
                    break;

                // PSRLW/PSRLD/PSRLQ/PSRAW/PSRAD/PSLLW/PSLLD/PSLLQ (0F 71-73 with /reg and imm8)
                case 0x71: case 0x72: case 0x73:
                    DecodeModRM(inst, ref pos);
                    inst.Immediate = _memory.ReadByte(pos);
                    inst.ImmediateSize = 1;
                    pos += 1;
                    break;

                // MOVNTDQ/MOVNTI/MOVNTPS (0F C3/E7/2B)
                case 0x2B: case 0xC3: case 0xE7:
                    DecodeModRM(inst, ref pos);
                    break;

                // LFENCE (0F AE /5), MFENCE (/6), SFENCE (/7) — memory fences
                case 0xAE:
                    DecodeModRM(inst, ref pos);
                    break;

                // UCOMISS xmm,xmm/m32 (0F 2E), UCOMISD (66 0F 2E)
                // COMISS (0F 2F), COMISD (66 0F 2F)
                case 0x2E: case 0x2F:
                    DecodeModRM(inst, ref pos);
                    break;

                // CVTSI2SS (F3 0F 2A), CVTSI2SD (F2 0F 2A)
                case 0x2A:
                    DecodeModRM(inst, ref pos);
                    break;

                // CVTTSS2SI (F3 0F 2C), CVTTSD2SI (F2 0F 2C)
                case 0x2C:
                    DecodeModRM(inst, ref pos);
                    break;

                // CVTSS2SI (F3 0F 2D), CVTSD2SI (F2 0F 2D)
                case 0x2D:
                    DecodeModRM(inst, ref pos);
                    break;

                // SQRTPS (0F 51), SQRTSS (F3 0F 51), SQRTPD (66 0F 51), SQRTSD (F2 0F 51)
                case 0x51:
                    DecodeModRM(inst, ref pos);
                    break;

                // ANDPS (0F 54), ANDNPS (0F 55), ORPS (0F 56)
                case 0x54: case 0x55: case 0x56:
                    DecodeModRM(inst, ref pos);
                    break;

                // PMOVMSKB (66 0F D7)
                case 0xD7:
                    DecodeModRM(inst, ref pos);
                    break;

                // PMINUB (66 0F DA), PMAXUB (66 0F DE)
                case 0xDA: case 0xDE:
                    DecodeModRM(inst, ref pos);
                    break;

                // PAVGB (66 0F E0), PAVGW (66 0F E3)
                case 0xE0: case 0xE3:
                    DecodeModRM(inst, ref pos);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Decode ModRM byte and optional SIB + displacement.
        /// </summary>
        private void DecodeModRM(DecodedInstruction inst, ref ulong pos)
        {
            inst.HasModRM = true;
            inst.ModRM = _memory.ReadByte(pos);
            pos++;

            int mod = (inst.ModRM >> 6) & 3;
            int rm = inst.ModRM & 7;

            // Check for SIB byte (rm == 4 in 64-bit mode, except mod == 3)
            if (mod != 3 && rm == 4)
            {
                inst.HasSIB = true;
                inst.SIB = _memory.ReadByte(pos);
                pos++;

                int sibBase = inst.SIB & 7;
                // SIB base == 5 with mod == 0 means disp32 with no base
                if (mod == 0 && sibBase == 5)
                {
                    inst.Displacement = (int)_memory.ReadUInt32(pos);
                    inst.DisplacementSize = 4;
                    pos += 4;
                }
            }

            // Displacement based on mod
            if (mod == 0 && rm == 5)
            {
                // RIP-relative addressing in 64-bit mode
                inst.Displacement = (int)_memory.ReadUInt32(pos);
                inst.DisplacementSize = 4;
                pos += 4;
            }
            else if (mod == 1)
            {
                inst.Displacement = (sbyte)_memory.ReadByte(pos);
                inst.DisplacementSize = 1;
                pos += 1;
            }
            else if (mod == 2)
            {
                inst.Displacement = (int)_memory.ReadUInt32(pos);
                inst.DisplacementSize = 4;
                pos += 4;
            }
        }
    }
}
