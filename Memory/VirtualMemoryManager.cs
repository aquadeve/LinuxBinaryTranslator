// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Virtual memory manager implementing Linux-style process address space.
// Models the kernel's mm_struct concepts for managing memory regions
// (vm_area_struct) within the translator's managed heap.

using System;
using System.Collections.Generic;
using System.Linq;

namespace LinuxBinaryTranslator.Memory
{
    /// <summary>
    /// Memory protection flags matching Linux PROT_* from kernel mman-common.h.
    /// </summary>
    [Flags]
    public enum MemoryProtection
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4,
        ReadWrite = Read | Write,
        ReadExecute = Read | Execute,
        ReadWriteExecute = Read | Write | Execute,
    }

    /// <summary>
    /// A virtual memory region, analogous to the kernel's vm_area_struct.
    /// </summary>
    public sealed class MemoryRegion
    {
        public ulong Start { get; }
        public ulong Size { get; }
        public ulong End => Start + Size;
        public MemoryProtection Protection { get; set; }
        public byte[] Data { get; }

        public MemoryRegion(ulong start, ulong size, MemoryProtection protection)
        {
            Start = start;
            Size = size;
            Protection = protection;
            Data = new byte[size];
        }

        public bool Contains(ulong address) => address >= Start && address < End;
        public bool Overlaps(ulong addr, ulong sz) => addr < End && (addr + sz) > Start;
    }

    /// <summary>
    /// Manages the virtual address space of a translated Linux process.
    /// Implements Linux-style memory management concepts:
    /// - mmap/munmap for region allocation
    /// - brk for heap management
    /// - Page-aligned allocations
    /// - Protection tracking per region
    ///
    /// Memory is backed by managed byte arrays rather than real virtual memory,
    /// ensuring full compatibility with the UWP sandbox and Xbox One restrictions.
    /// </summary>
    public sealed class VirtualMemoryManager
    {
        public const ulong PageSize = 4096;
        public const ulong PageMask = ~(PageSize - 1);

        // Sorted list of memory regions, keyed by start address
        private readonly SortedList<ulong, MemoryRegion> _regions = new SortedList<ulong, MemoryRegion>();

        // Current program break for brk() syscall
        private ulong _brkBase;
        private ulong _brkCurrent;

        // Next address hint for mmap with no fixed address
        private ulong _mmapHint = 0x7F0000000000UL; // High address space like Linux

        // Total allocated memory tracking
        private ulong _totalAllocated;
        private readonly ulong _maxMemory;

        /// <summary>
        /// Current program break address.
        /// </summary>
        public ulong CurrentBrk => _brkCurrent;

        public VirtualMemoryManager(ulong maxMemory = 512UL * 1024 * 1024) // 512 MB default
        {
            _maxMemory = maxMemory;
        }

        /// <summary>
        /// Initialize the program break (called after ELF loading).
        /// Mirrors kernel's setup of mm_struct->start_brk / brk.
        /// </summary>
        public void InitializeBrk(ulong brkBase)
        {
            _brkBase = AlignUp(brkBase, PageSize);
            _brkCurrent = _brkBase;
        }

        /// <summary>
        /// Implement Linux brk() syscall.
        /// Grows or shrinks the process data segment.
        /// Returns the new brk on success, or current brk on failure.
        /// </summary>
        public ulong Brk(ulong newBrk)
        {
            if (newBrk == 0)
                return _brkCurrent;

            newBrk = AlignUp(newBrk, PageSize);

            if (newBrk < _brkBase)
                return _brkCurrent;

            if (newBrk > _brkCurrent)
            {
                // Expand: map new pages
                ulong expandSize = newBrk - _brkCurrent;
                if (_totalAllocated + expandSize > _maxMemory)
                    return _brkCurrent; // Out of memory

                Map(_brkCurrent, expandSize, MemoryProtection.ReadWrite);
                _brkCurrent = newBrk;
            }
            else if (newBrk < _brkCurrent)
            {
                // Shrink: unmap excess pages
                Unmap(newBrk, _brkCurrent - newBrk);
                _brkCurrent = newBrk;
            }

            return _brkCurrent;
        }

        /// <summary>
        /// Map a region of virtual memory (mmap-style).
        /// If address is 0, finds a free region automatically.
        /// </summary>
        public ulong Map(ulong address, ulong size, MemoryProtection protection)
        {
            size = AlignUp(size, PageSize);

            if (size == 0)
                return unchecked((ulong)-1L);

            if (_totalAllocated + size > _maxMemory)
                return unchecked((ulong)-1L);

            if (address == 0)
            {
                address = FindFreeRegion(size);
                if (address == 0)
                    return unchecked((ulong)-1L);
            }
            else
            {
                address = AlignDown(address, PageSize);
            }

            // Remove any overlapping regions
            RemoveOverlapping(address, size);

            var region = new MemoryRegion(address, size, protection);
            _regions[address] = region;
            _totalAllocated += size;

            return address;
        }

        /// <summary>
        /// Check whether an entire address range is currently unmapped.
        /// </summary>
        public bool IsRangeFree(ulong address, ulong size)
        {
            address = AlignDown(address, PageSize);
            size = AlignUp(size, PageSize);

            if (size == 0)
                return false;

            foreach (var kvp in _regions)
            {
                if (kvp.Value.Overlaps(address, size))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Unmap a region of virtual memory (munmap-style).
        /// </summary>
        public int Unmap(ulong address, ulong size)
        {
            address = AlignDown(address, PageSize);
            size = AlignUp(size, PageSize);

            var toRemove = new List<ulong>();
            var toAdd = new List<MemoryRegion>();

            foreach (var kvp in _regions)
            {
                var region = kvp.Value;
                if (!region.Overlaps(address, size))
                    continue;

                toRemove.Add(kvp.Key);

                // Handle partial unmapping - split regions if needed
                if (region.Start < address)
                {
                    // Keep the portion before the unmap range
                    ulong keepSize = address - region.Start;
                    var keepRegion = new MemoryRegion(region.Start, keepSize, region.Protection);
                    Array.Copy(region.Data, 0, keepRegion.Data, 0, (int)keepSize);
                    toAdd.Add(keepRegion);
                }

                ulong unmapEnd = address + size;
                if (region.End > unmapEnd)
                {
                    // Keep the portion after the unmap range
                    ulong keepStart = unmapEnd;
                    ulong keepSize = region.End - unmapEnd;
                    var keepRegion = new MemoryRegion(keepStart, keepSize, region.Protection);
                    ulong srcOffset = unmapEnd - region.Start;
                    Array.Copy(region.Data, (int)srcOffset, keepRegion.Data, 0, (int)keepSize);
                    toAdd.Add(keepRegion);
                }
            }

            foreach (var key in toRemove)
            {
                _totalAllocated -= _regions[key].Size;
                _regions.Remove(key);
            }

            foreach (var region in toAdd)
            {
                _regions[region.Start] = region;
                _totalAllocated += region.Size;
            }

            return 0;
        }

        /// <summary>
        /// Change protection of a memory region (mprotect-style).
        /// </summary>
        public int Protect(ulong address, ulong size, MemoryProtection protection)
        {
            address = AlignDown(address, PageSize);
            size = AlignUp(size, PageSize);

            foreach (var kvp in _regions)
            {
                if (kvp.Value.Overlaps(address, size))
                    kvp.Value.Protection = protection;
            }

            return 0;
        }

        /// <summary>
        /// Read bytes from virtual memory.
        /// </summary>
        public byte[] Read(ulong address, ulong count)
        {
            var result = new byte[(int)count];
            ulong bytesRead = 0;

            while (bytesRead < count)
            {
                ulong currentAddr = address + bytesRead;
                var region = FindRegion(currentAddr);
                if (region == null)
                    break;

                ulong offset = currentAddr - region.Start;
                ulong available = region.Size - offset;
                ulong toRead = Math.Min(available, count - bytesRead);
                Array.Copy(region.Data, (int)offset, result, (int)bytesRead, (int)toRead);
                bytesRead += toRead;
            }

            return result;
        }

        /// <summary>
        /// Read a single byte from virtual memory.
        /// </summary>
        public byte ReadByte(ulong address)
        {
            var region = FindRegion(address);
            if (region == null)
                return 0;

            ulong offset = address - region.Start;
            return region.Data[offset];
        }

        /// <summary>
        /// Read a 16-bit value from virtual memory (little-endian).
        /// </summary>
        public ushort ReadUInt16(ulong address)
        {
            var region = FindRegion(address);
            if (region == null) return 0;
            ulong offset = address - region.Start;
            return (ushort)(region.Data[offset] | (region.Data[offset + 1] << 8));
        }

        /// <summary>
        /// Read a 32-bit value from virtual memory (little-endian).
        /// </summary>
        public uint ReadUInt32(ulong address)
        {
            var region = FindRegion(address);
            if (region == null) return 0;
            ulong offset = address - region.Start;
            return (uint)(region.Data[offset]
                | (region.Data[offset + 1] << 8)
                | (region.Data[offset + 2] << 16)
                | (region.Data[offset + 3] << 24));
        }

        /// <summary>
        /// Read a 64-bit value from virtual memory (little-endian).
        /// </summary>
        public ulong ReadUInt64(ulong address)
        {
            var region = FindRegion(address);
            if (region == null) return 0;
            ulong offset = address - region.Start;
            ulong lo = ReadUInt32(address);
            ulong hi = ReadUInt32(address + 4);
            return lo | (hi << 32);
        }

        /// <summary>
        /// Write bytes to virtual memory.
        /// </summary>
        public void Write(ulong address, byte[] data)
        {
            ulong bytesWritten = 0;
            ulong count = (ulong)data.Length;

            while (bytesWritten < count)
            {
                ulong currentAddr = address + bytesWritten;
                var region = FindRegion(currentAddr);
                if (region == null)
                    break;

                ulong offset = currentAddr - region.Start;
                ulong available = region.Size - offset;
                ulong toWrite = Math.Min(available, count - bytesWritten);
                Array.Copy(data, (int)bytesWritten, region.Data, (int)offset, (int)toWrite);
                bytesWritten += toWrite;
            }
        }

        /// <summary>
        /// Write a single byte to virtual memory.
        /// </summary>
        public void WriteByte(ulong address, byte value)
        {
            var region = FindRegion(address);
            if (region == null) return;
            region.Data[address - region.Start] = value;
        }

        /// <summary>
        /// Write a 16-bit value to virtual memory (little-endian).
        /// </summary>
        public void WriteUInt16(ulong address, ushort value)
        {
            var region = FindRegion(address);
            if (region == null) return;
            ulong offset = address - region.Start;
            region.Data[offset] = (byte)value;
            region.Data[offset + 1] = (byte)(value >> 8);
        }

        /// <summary>
        /// Write a 32-bit value to virtual memory (little-endian).
        /// </summary>
        public void WriteUInt32(ulong address, uint value)
        {
            var region = FindRegion(address);
            if (region == null) return;
            ulong offset = address - region.Start;
            region.Data[offset] = (byte)value;
            region.Data[offset + 1] = (byte)(value >> 8);
            region.Data[offset + 2] = (byte)(value >> 16);
            region.Data[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// Write a 64-bit value to virtual memory (little-endian).
        /// </summary>
        public void WriteUInt64(ulong address, ulong value)
        {
            WriteUInt32(address, (uint)value);
            WriteUInt32(address + 4, (uint)(value >> 32));
        }

        /// <summary>
        /// Zero-fill a region of virtual memory.
        /// </summary>
        public void Zero(ulong address, ulong size)
        {
            Write(address, new byte[size]);
        }

        /// <summary>
        /// Check if an address is in a mapped region.
        /// </summary>
        public bool IsMapped(ulong address)
        {
            return FindRegion(address) != null;
        }

        /// <summary>
        /// Find the memory region containing the given address.
        /// </summary>
        public MemoryRegion? FindRegion(ulong address)
        {
            // Binary search through sorted regions
            foreach (var kvp in _regions)
            {
                if (kvp.Value.Contains(address))
                    return kvp.Value;
                if (kvp.Key > address)
                    break;
            }
            return null;
        }

        private ulong FindFreeRegion(ulong size)
        {
            ulong candidate = _mmapHint;

            // Simple strategy: scan downward from hint
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                bool conflict = false;
                foreach (var kvp in _regions)
                {
                    if (kvp.Value.Overlaps(candidate, size))
                    {
                        conflict = true;
                        break;
                    }
                }

                if (!conflict)
                {
                    _mmapHint = candidate + size;
                    return candidate;
                }

                candidate += size + PageSize;
            }

            return 0;
        }

        /// <summary>
        /// Remove regions that overlap with [address, address+size), preserving
        /// any data in non-overlapping portions by splitting regions. This is
        /// critical for ELF loading where adjacent PT_LOAD segments share a
        /// page boundary: the second Map() must not destroy data loaded by the
        /// first segment (e.g., the ELF header at the base address).
        /// </summary>
        private void RemoveOverlapping(ulong address, ulong size)
        {
            ulong end = address + size;
            var toRemove = new List<ulong>();
            var toAdd = new List<MemoryRegion>();

            foreach (var kvp in _regions)
            {
                var region = kvp.Value;
                if (!region.Overlaps(address, size))
                    continue;

                toRemove.Add(kvp.Key);

                // Preserve the portion of the existing region that lies BEFORE
                // the new mapping range.
                if (region.Start < address)
                {
                    ulong keepSize = address - region.Start;
                    var keepRegion = new MemoryRegion(region.Start, keepSize, region.Protection);
                    Array.Copy(region.Data, 0, keepRegion.Data, 0, (int)keepSize);
                    toAdd.Add(keepRegion);
                }

                // Preserve the portion of the existing region that lies AFTER
                // the new mapping range.
                if (region.End > end)
                {
                    ulong keepStart = end;
                    ulong keepSize = region.End - end;
                    var keepRegion = new MemoryRegion(keepStart, keepSize, region.Protection);
                    ulong srcOffset = end - region.Start;
                    Array.Copy(region.Data, (int)srcOffset, keepRegion.Data, 0, (int)keepSize);
                    toAdd.Add(keepRegion);
                }
            }

            foreach (var key in toRemove)
            {
                _totalAllocated -= _regions[key].Size;
                _regions.Remove(key);
            }

            foreach (var region in toAdd)
            {
                _regions[region.Start] = region;
                _totalAllocated += region.Size;
            }
        }

        public static ulong AlignDown(ulong value, ulong alignment)
            => value & ~(alignment - 1);

        public static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);
    }
}
