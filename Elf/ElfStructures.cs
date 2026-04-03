// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// ELF binary format structures for 64-bit executables.
// Derived from the Linux kernel source:
//   include/uapi/linux/elf.h
//   include/uapi/linux/elf-em.h

using System;
using System.Runtime.InteropServices;

namespace LinuxBinaryTranslator.Elf
{
    /// <summary>
    /// ELF identification bytes and magic numbers from kernel elf.h.
    /// </summary>
    public static class ElfConstants
    {
        // e_ident[] indices
        public const int EI_MAG0 = 0;
        public const int EI_MAG1 = 1;
        public const int EI_MAG2 = 2;
        public const int EI_MAG3 = 3;
        public const int EI_CLASS = 4;
        public const int EI_DATA = 5;
        public const int EI_VERSION = 6;
        public const int EI_OSABI = 7;
        public const int EI_ABIVERSION = 8;
        public const int EI_PAD = 9;
        public const int EI_NIDENT = 16;

        // ELF magic
        public const byte ELFMAG0 = 0x7F;
        public const byte ELFMAG1 = (byte)'E';
        public const byte ELFMAG2 = (byte)'L';
        public const byte ELFMAG3 = (byte)'F';

        // ELF class
        public const byte ELFCLASSNONE = 0;
        public const byte ELFCLASS32 = 1;
        public const byte ELFCLASS64 = 2;

        // ELF data encoding
        public const byte ELFDATANONE = 0;
        public const byte ELFDATA2LSB = 1;
        public const byte ELFDATA2MSB = 2;

        // ELF version
        public const byte EV_CURRENT = 1;

        // OS/ABI
        public const byte ELFOSABI_NONE = 0;
        public const byte ELFOSABI_LINUX = 3;
        public const byte ELFOSABI_GNU = 3;

        // e_type values
        public const ushort ET_NONE = 0;
        public const ushort ET_REL = 1;
        public const ushort ET_EXEC = 2;
        public const ushort ET_DYN = 3;
        public const ushort ET_CORE = 4;

        // e_machine values (from elf-em.h)
        public const ushort EM_386 = 3;
        public const ushort EM_ARM = 40;
        public const ushort EM_X86_64 = 62;
        public const ushort EM_AARCH64 = 183;

        // Program header types (p_type)
        public const uint PT_NULL = 0;
        public const uint PT_LOAD = 1;
        public const uint PT_DYNAMIC = 2;
        public const uint PT_INTERP = 3;
        public const uint PT_NOTE = 4;
        public const uint PT_SHLIB = 5;
        public const uint PT_PHDR = 6;
        public const uint PT_TLS = 7;
        public const uint PT_GNU_EH_FRAME = 0x6474E550;
        public const uint PT_GNU_STACK = 0x6474E551;
        public const uint PT_GNU_RELRO = 0x6474E552;

        // Program header flags (p_flags)
        public const uint PF_X = 0x1;
        public const uint PF_W = 0x2;
        public const uint PF_R = 0x4;

        // Section header types (sh_type)
        public const uint SHT_NULL = 0;
        public const uint SHT_PROGBITS = 1;
        public const uint SHT_SYMTAB = 2;
        public const uint SHT_STRTAB = 3;
        public const uint SHT_RELA = 4;
        public const uint SHT_HASH = 5;
        public const uint SHT_DYNAMIC = 6;
        public const uint SHT_NOTE = 7;
        public const uint SHT_NOBITS = 8;
        public const uint SHT_REL = 9;
        public const uint SHT_DYNSYM = 11;

        // Section header flags (sh_flags)
        public const ulong SHF_WRITE = 0x1;
        public const ulong SHF_ALLOC = 0x2;
        public const ulong SHF_EXECINSTR = 0x4;
        public const ulong SHF_MERGE = 0x10;
        public const ulong SHF_STRINGS = 0x20;

        // Auxiliary vector types (for ELF loader)
        public const ulong AT_NULL = 0;
        public const ulong AT_IGNORE = 1;
        public const ulong AT_EXECFD = 2;
        public const ulong AT_PHDR = 3;
        public const ulong AT_PHENT = 4;
        public const ulong AT_PHNUM = 5;
        public const ulong AT_PAGESZ = 6;
        public const ulong AT_BASE = 7;
        public const ulong AT_FLAGS = 8;
        public const ulong AT_ENTRY = 9;
        public const ulong AT_UID = 11;
        public const ulong AT_EUID = 12;
        public const ulong AT_GID = 13;
        public const ulong AT_EGID = 14;
        public const ulong AT_PLATFORM = 15;
        public const ulong AT_HWCAP = 16;
        public const ulong AT_CLKTCK = 17;
        public const ulong AT_SECURE = 23;
        public const ulong AT_RANDOM = 25;
        public const ulong AT_EXECFN = 31;
    }

    /// <summary>
    /// 64-bit ELF file header.
    /// Layout matches kernel struct elf64_hdr from include/uapi/linux/elf.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64Header
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] e_ident;
        public ushort e_type;
        public ushort e_machine;
        public uint e_version;
        public ulong e_entry;
        public ulong e_phoff;
        public ulong e_shoff;
        public uint e_flags;
        public ushort e_ehsize;
        public ushort e_phentsize;
        public ushort e_phnum;
        public ushort e_shentsize;
        public ushort e_shnum;
        public ushort e_shstrndx;

        public bool IsValid()
        {
            return e_ident != null
                && e_ident.Length >= ElfConstants.EI_NIDENT
                && e_ident[ElfConstants.EI_MAG0] == ElfConstants.ELFMAG0
                && e_ident[ElfConstants.EI_MAG1] == ElfConstants.ELFMAG1
                && e_ident[ElfConstants.EI_MAG2] == ElfConstants.ELFMAG2
                && e_ident[ElfConstants.EI_MAG3] == ElfConstants.ELFMAG3;
        }

        public bool Is64Bit() => e_ident[ElfConstants.EI_CLASS] == ElfConstants.ELFCLASS64;
        public bool IsLittleEndian() => e_ident[ElfConstants.EI_DATA] == ElfConstants.ELFDATA2LSB;
        public bool IsExecutable() => e_type == ElfConstants.ET_EXEC;
        public bool IsSharedObject() => e_type == ElfConstants.ET_DYN;
        public bool IsX86_64() => e_machine == ElfConstants.EM_X86_64;
    }

    /// <summary>
    /// 64-bit ELF program header.
    /// Layout matches kernel struct elf64_phdr from include/uapi/linux/elf.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64ProgramHeader
    {
        public uint p_type;
        public uint p_flags;
        public ulong p_offset;
        public ulong p_vaddr;
        public ulong p_paddr;
        public ulong p_filesz;
        public ulong p_memsz;
        public ulong p_align;

        public bool IsLoadable => p_type == ElfConstants.PT_LOAD;
        public bool IsReadable => (p_flags & ElfConstants.PF_R) != 0;
        public bool IsWritable => (p_flags & ElfConstants.PF_W) != 0;
        public bool IsExecutable => (p_flags & ElfConstants.PF_X) != 0;
    }

    /// <summary>
    /// 64-bit ELF section header.
    /// Layout matches kernel struct elf64_shdr from include/uapi/linux/elf.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64SectionHeader
    {
        public uint sh_name;
        public uint sh_type;
        public ulong sh_flags;
        public ulong sh_addr;
        public ulong sh_offset;
        public ulong sh_size;
        public uint sh_link;
        public uint sh_info;
        public ulong sh_addralign;
        public ulong sh_entsize;
    }
}
