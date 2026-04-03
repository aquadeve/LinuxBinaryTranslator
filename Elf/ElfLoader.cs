// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// ELF binary loader. Parses and loads 64-bit Linux ELF executables into
// the virtual memory space for translation and execution.

using System;
using System.Collections.Generic;
using System.IO;
using LinuxBinaryTranslator.Memory;

namespace LinuxBinaryTranslator.Elf
{
    /// <summary>
    /// Represents a loaded ELF segment in virtual memory.
    /// </summary>
    public sealed class LoadedSegment
    {
        public ulong VirtualAddress { get; }
        public ulong MemorySize { get; }
        public ulong FileSize { get; }
        public bool Readable { get; }
        public bool Writable { get; }
        public bool Executable { get; }

        public LoadedSegment(ulong vaddr, ulong memsz, ulong filesz,
                             bool readable, bool writable, bool executable)
        {
            VirtualAddress = vaddr;
            MemorySize = memsz;
            FileSize = filesz;
            Readable = readable;
            Writable = writable;
            Executable = executable;
        }
    }

    /// <summary>
    /// Result of loading an ELF binary.
    /// </summary>
    public sealed class ElfLoadResult
    {
        public ulong EntryPoint { get; set; }
        public ulong ProgramHeaderAddress { get; set; }
        public ushort ProgramHeaderEntrySize { get; set; }
        public ushort ProgramHeaderCount { get; set; }
        public ulong BaseAddress { get; set; }
        public ulong BrkAddress { get; set; }
        public ushort Machine { get; set; }
        public List<LoadedSegment> Segments { get; } = new List<LoadedSegment>();
        public string? InterpreterPath { get; set; }
        public ulong InterpreterBase { get; set; }
    }

    /// <summary>
    /// Loads Linux ELF64 binaries into the translator's virtual memory.
    /// Parses ELF headers and program segments according to the format defined
    /// in the Linux kernel's include/uapi/linux/elf.h.
    /// </summary>
    public sealed class ElfLoader
    {
        private readonly VirtualMemoryManager _memory;

        public ElfLoader(VirtualMemoryManager memory)
        {
            _memory = memory;
        }

        /// <summary>
        /// Default load bias for PIE (ET_DYN) main executables.
        /// This mirrors the Linux kernel's default load address for PIE binaries
        /// (typically randomized around 0x555555554000 with ASLR).
        /// </summary>
        private const ulong PieDefaultLoadBias = 0x555555554000UL;

        /// <summary>
        /// Load an ELF binary from a byte array into virtual memory.
        /// </summary>
        public ElfLoadResult Load(byte[] elfData)
        {
            if (elfData == null || elfData.Length < 64)
                throw new ElfLoadException("Data too small to be a valid ELF binary");

            var header = ParseHeader(elfData);
            ValidateHeader(header);

            var programHeaders = ParseProgramHeaders(elfData, header);

            // For PIE (ET_DYN) main executables, apply a load bias so they
            // don't get mapped at address 0. The Linux kernel loads PIE
            // binaries at a high address (with ASLR, typically near 0x555555554000).
            // Without this, ld.so gets confused because the main binary's load
            // address is 0, which conflicts with null pointer semantics.
            ulong loadBias = header.IsSharedObject() ? PieDefaultLoadBias : 0;

            var result = new ElfLoadResult
            {
                EntryPoint = header.e_entry + loadBias,
                ProgramHeaderEntrySize = header.e_phentsize,
                ProgramHeaderCount = header.e_phnum,
                Machine = header.e_machine,
            };

            ulong lowestAddr = ulong.MaxValue;
            ulong highestAddr = 0;

            // Extract PT_INTERP (dynamic linker path) if present
            foreach (var phdr in programHeaders)
            {
                if (phdr.p_type == ElfConstants.PT_INTERP && phdr.p_filesz > 0)
                {
                    int interpLen = (int)Math.Min(phdr.p_filesz, 256) - 1; // -1 for null terminator
                    if (interpLen > 0 && phdr.p_offset + phdr.p_filesz <= (ulong)elfData.Length)
                    {
                        result.InterpreterPath = System.Text.Encoding.UTF8.GetString(
                            elfData, (int)phdr.p_offset, interpLen).TrimEnd('\0');
                    }
                }
            }

            // Load all PT_LOAD segments into virtual memory
            foreach (var phdr in programHeaders)
            {
                if (!phdr.IsLoadable || phdr.p_memsz == 0)
                    continue;

                ulong loadAddr = phdr.p_vaddr + loadBias;
                ulong alignedAddr = AlignDown(loadAddr, PageSize);
                ulong alignedEnd = AlignUp(loadAddr + phdr.p_memsz, PageSize);
                ulong regionSize = alignedEnd - alignedAddr;

                // Track address range
                if (alignedAddr < lowestAddr)
                    lowestAddr = alignedAddr;
                if (alignedEnd > highestAddr)
                    highestAddr = alignedEnd;

                // Allocate virtual memory region
                var protection = MemoryProtection.None;
                if (phdr.IsReadable) protection |= MemoryProtection.Read;
                if (phdr.IsWritable) protection |= MemoryProtection.Write;
                if (phdr.IsExecutable) protection |= MemoryProtection.Execute;

                _memory.Map(alignedAddr, regionSize, protection);

                // Copy file data into the mapped region
                if (phdr.p_filesz > 0)
                {
                    ulong fileOffset = phdr.p_offset;
                    ulong copyLen = Math.Min(phdr.p_filesz, (ulong)elfData.Length - fileOffset);
                    var segment = new byte[(int)copyLen];
                    Array.Copy(elfData, (int)fileOffset, segment, 0, (int)copyLen);
                    _memory.Write(loadAddr, segment);
                }

                // Zero-fill BSS (memory beyond file data)
                if (phdr.p_memsz > phdr.p_filesz)
                {
                    ulong bssStart = loadAddr + phdr.p_filesz;
                    ulong bssSize = phdr.p_memsz - phdr.p_filesz;
                    _memory.Zero(bssStart, bssSize);
                }

                result.Segments.Add(new LoadedSegment(
                    loadAddr, phdr.p_memsz, phdr.p_filesz,
                    phdr.IsReadable, phdr.IsWritable, phdr.IsExecutable));
            }

            result.BaseAddress = lowestAddr == ulong.MaxValue ? 0 : lowestAddr;
            result.BrkAddress = highestAddr;

            // Store program headers in memory for auxvec AT_PHDR
            if (header.e_phoff > 0 && header.e_phnum > 0)
                result.ProgramHeaderAddress = ComputeProgramHeaderAddress(header, programHeaders, loadBias);

            return result;
        }

        /// <summary>
        /// Load the ELF interpreter (dynamic linker, e.g., /lib64/ld-linux-x86-64.so.2)
        /// at a base address above the main binary. The interpreter is loaded as a
        /// position-independent binary with a base offset applied to all addresses.
        /// </summary>
        public ElfLoadResult LoadInterpreter(byte[] interpData, ulong baseAddress)
        {
            if (interpData == null || interpData.Length < 64)
                throw new ElfLoadException("Interpreter data too small");

            var header = ParseHeader(interpData);
            ValidateHeader(header);

            var programHeaders = ParseProgramHeaders(interpData, header);
            var result = new ElfLoadResult
            {
                ProgramHeaderEntrySize = header.e_phentsize,
                ProgramHeaderCount = header.e_phnum,
                Machine = header.e_machine,
            };

            // For ET_DYN (shared objects), apply base address offset
            ulong baseOffset = header.IsSharedObject() ? baseAddress : 0;
            result.EntryPoint = header.e_entry + baseOffset;
            result.InterpreterBase = baseOffset;

            ulong lowestAddr = ulong.MaxValue;
            ulong highestAddr = 0;

            foreach (var phdr in programHeaders)
            {
                if (!phdr.IsLoadable || phdr.p_memsz == 0)
                    continue;

                ulong loadAddr = phdr.p_vaddr + baseOffset;
                ulong alignedAddr = AlignDown(loadAddr, PageSize);
                ulong alignedEnd = AlignUp(loadAddr + phdr.p_memsz, PageSize);
                ulong regionSize = alignedEnd - alignedAddr;

                if (alignedAddr < lowestAddr)
                    lowestAddr = alignedAddr;
                if (alignedEnd > highestAddr)
                    highestAddr = alignedEnd;

                var protection = MemoryProtection.None;
                if (phdr.IsReadable) protection |= MemoryProtection.Read;
                if (phdr.IsWritable) protection |= MemoryProtection.Write;
                if (phdr.IsExecutable) protection |= MemoryProtection.Execute;

                _memory.Map(alignedAddr, regionSize, protection);

                if (phdr.p_filesz > 0)
                {
                    ulong fileOffset = phdr.p_offset;
                    ulong copyLen = Math.Min(phdr.p_filesz, (ulong)interpData.Length - fileOffset);
                    var segment = new byte[(int)copyLen];
                    Array.Copy(interpData, (int)fileOffset, segment, 0, (int)copyLen);
                    _memory.Write(loadAddr, segment);
                }

                if (phdr.p_memsz > phdr.p_filesz)
                {
                    ulong bssStart = loadAddr + phdr.p_filesz;
                    ulong bssSize = phdr.p_memsz - phdr.p_filesz;
                    _memory.Zero(bssStart, bssSize);
                }

                result.Segments.Add(new LoadedSegment(
                    loadAddr, phdr.p_memsz, phdr.p_filesz,
                    phdr.IsReadable, phdr.IsWritable, phdr.IsExecutable));
            }

            result.BaseAddress = lowestAddr == ulong.MaxValue ? baseAddress : lowestAddr;
            result.BrkAddress = highestAddr;

            if (header.e_phoff > 0 && header.e_phnum > 0)
                result.ProgramHeaderAddress = ComputeProgramHeaderAddress(header, programHeaders, baseOffset);

            return result;
        }

        /// <summary>
        /// Parse the ELF64 file header from raw bytes.
        /// </summary>
        private static Elf64Header ParseHeader(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            var header = new Elf64Header
            {
                e_ident = reader.ReadBytes(ElfConstants.EI_NIDENT),
                e_type = reader.ReadUInt16(),
                e_machine = reader.ReadUInt16(),
                e_version = reader.ReadUInt32(),
                e_entry = reader.ReadUInt64(),
                e_phoff = reader.ReadUInt64(),
                e_shoff = reader.ReadUInt64(),
                e_flags = reader.ReadUInt32(),
                e_ehsize = reader.ReadUInt16(),
                e_phentsize = reader.ReadUInt16(),
                e_phnum = reader.ReadUInt16(),
                e_shentsize = reader.ReadUInt16(),
                e_shnum = reader.ReadUInt16(),
                e_shstrndx = reader.ReadUInt16()
            };
            return header;
        }

        /// <summary>
        /// Validate the ELF header for compatibility.
        /// </summary>
        private static void ValidateHeader(Elf64Header header)
        {
            if (!header.IsValid())
                throw new ElfLoadException("Invalid ELF magic number");

            if (!header.Is64Bit())
                throw new ElfLoadException("Only 64-bit ELF binaries are supported");

            if (!header.IsLittleEndian())
                throw new ElfLoadException("Only little-endian ELF binaries are supported");

            if (!header.IsX86_64())
                throw new ElfLoadException(
                    $"Unsupported machine type: {header.e_machine}. Only x86_64 (EM_X86_64={ElfConstants.EM_X86_64}) is supported");

            if (!header.IsExecutable() && !header.IsSharedObject())
                throw new ElfLoadException("ELF binary must be ET_EXEC or ET_DYN (static or PIE executable)");
        }

        /// <summary>
        /// Parse all program headers from the ELF file.
        /// </summary>
        private static List<Elf64ProgramHeader> ParseProgramHeaders(byte[] data, Elf64Header header)
        {
            var headers = new List<Elf64ProgramHeader>();
            long offset = (long)header.e_phoff;

            for (int i = 0; i < header.e_phnum; i++)
            {
                if (offset + header.e_phentsize > data.Length)
                    throw new ElfLoadException($"Program header {i} extends beyond file");

                using var reader = new BinaryReader(new MemoryStream(data, (int)offset, header.e_phentsize));
                var phdr = new Elf64ProgramHeader
                {
                    p_type = reader.ReadUInt32(),
                    p_flags = reader.ReadUInt32(),
                    p_offset = reader.ReadUInt64(),
                    p_vaddr = reader.ReadUInt64(),
                    p_paddr = reader.ReadUInt64(),
                    p_filesz = reader.ReadUInt64(),
                    p_memsz = reader.ReadUInt64(),
                    p_align = reader.ReadUInt64()
                };
                headers.Add(phdr);
                offset += header.e_phentsize;
            }

            return headers;
        }

        private static ulong ComputeProgramHeaderAddress(Elf64Header header, List<Elf64ProgramHeader> programHeaders, ulong loadBias)
        {
            foreach (var phdr in programHeaders)
            {
                if (phdr.p_type == ElfConstants.PT_PHDR)
                    return loadBias + phdr.p_vaddr;
            }

            ulong phdrTableFileStart = header.e_phoff;
            ulong phdrTableFileEnd = phdrTableFileStart + (ulong)(header.e_phentsize * header.e_phnum);

            foreach (var phdr in programHeaders)
            {
                if (!phdr.IsLoadable || phdr.p_filesz == 0)
                    continue;

                ulong segmentFileStart = phdr.p_offset;
                ulong segmentFileEnd = phdr.p_offset + phdr.p_filesz;
                if (phdrTableFileStart >= segmentFileStart && phdrTableFileEnd <= segmentFileEnd)
                    return loadBias + phdr.p_vaddr + (phdrTableFileStart - segmentFileStart);
            }

            return loadBias + header.e_phoff;
        }

        private const ulong PageSize = 4096;

        private static ulong AlignDown(ulong value, ulong alignment)
            => value & ~(alignment - 1);

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>
    /// Exception thrown when ELF loading fails.
    /// </summary>
    public class ElfLoadException : Exception
    {
        public ElfLoadException(string message) : base(message) { }
        public ElfLoadException(string message, Exception inner) : base(message, inner) { }
    }
}
