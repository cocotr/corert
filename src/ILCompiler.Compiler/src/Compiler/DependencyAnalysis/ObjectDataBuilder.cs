// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using Internal.TypeSystem;
using Internal.Runtime;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler.DependencyAnalysis
{
    public struct ObjectDataBuilder : Internal.Runtime.ITargetBinaryWriter
    {
        public ObjectDataBuilder(NodeFactory factory, bool relocsOnly)
        {
            _target = factory.Target;
            _data = new ArrayBuilder<byte>();
            _relocs = new ArrayBuilder<Relocation>();
            Alignment = 1;
            _definedSymbols = new ArrayBuilder<ISymbolDefinitionNode>();
#if DEBUG
            _numReservations = 0;
            _checkAllSymbolDependenciesMustBeMarked = !relocsOnly;
#endif
        }

        private TargetDetails _target;
        private ArrayBuilder<Relocation> _relocs;
        private ArrayBuilder<byte> _data;
        public int Alignment { get; private set; }
        private ArrayBuilder<ISymbolDefinitionNode> _definedSymbols;

#if DEBUG
        private int _numReservations;
        private bool _checkAllSymbolDependenciesMustBeMarked;
#endif

        public int CountBytes
        {
            get
            {
                return _data.Count;
            }
        }

        public int TargetPointerSize
        {
            get
            {
                return _target.PointerSize;
            }
        }

        /// <summary>
        /// Raise the alignment requirement of this object to <paramref name="align"/>. This has no effect
        /// if the alignment requirement is already larger than <paramref name="align"/>.
        /// </summary>
        public void RequireInitialAlignment(int align)
        {
            Alignment = Math.Max(align, Alignment);
        }

        /// <summary>
        /// Raise the alignment requirement of this object to the target pointer size. This has no effect
        /// if the alignment requirement is already larger than a pointer size.
        /// </summary>
        public void RequireInitialPointerAlignment()
        {
            RequireInitialAlignment(_target.PointerSize);
        }

        public void EmitByte(byte emit)
        {
            _data.Add(emit);
        }

        public void EmitShort(short emit)
        {
            EmitByte((byte)(emit & 0xFF));
            EmitByte((byte)((emit >> 8) & 0xFF));
        }

        public void EmitInt(int emit)
        {
            EmitByte((byte)(emit & 0xFF));
            EmitByte((byte)((emit >> 8) & 0xFF));
            EmitByte((byte)((emit >> 16) & 0xFF));
            EmitByte((byte)((emit >> 24) & 0xFF));
        }

        public void EmitLong(long emit)
        {
            EmitByte((byte)(emit & 0xFF));
            EmitByte((byte)((emit >> 8) & 0xFF));
            EmitByte((byte)((emit >> 16) & 0xFF));
            EmitByte((byte)((emit >> 24) & 0xFF));
            EmitByte((byte)((emit >> 32) & 0xFF));
            EmitByte((byte)((emit >> 40) & 0xFF));
            EmitByte((byte)((emit >> 48) & 0xFF));
            EmitByte((byte)((emit >> 56) & 0xFF));
        }

        public void EmitNaturalInt(int emit)
        {
            if (_target.PointerSize == 8)
            {
                EmitLong(emit);
            }
            else
            {
                Debug.Assert(_target.PointerSize == 4);
                EmitInt(emit);
            }
        }

        public void EmitHalfNaturalInt(short emit)
        {
            if (_target.PointerSize == 8)
            {
                EmitInt(emit);
            }
            else
            {
                Debug.Assert(_target.PointerSize == 4);
                EmitShort(emit);
            }
        }

        public void EmitCompressedUInt(uint emit)
        {
            if (emit < 128)
            {
                EmitByte((byte)(emit * 2 + 0));
            }
            else if (emit < 128 * 128)
            {
                EmitByte((byte)(emit * 4 + 1));
                EmitByte((byte)(emit >> 6));
            }
            else if (emit < 128 * 128 * 128)
            {
                EmitByte((byte)(emit * 8 + 3));
                EmitByte((byte)(emit >> 5));
                EmitByte((byte)(emit >> 13));
            }
            else if (emit < 128 * 128 * 128 * 128)
            {
                EmitByte((byte)(emit * 16 + 7));
                EmitByte((byte)(emit >> 4));
                EmitByte((byte)(emit >> 12));
                EmitByte((byte)(emit >> 20));
            }
            else
            {
                EmitByte((byte)15);
                EmitInt((int)emit);
            }
        }

        public void EmitBytes(byte[] bytes)
        {
            _data.Append(bytes);
        }

        public void EmitZeroPointer()
        {
            _data.ZeroExtend(_target.PointerSize);
        }

        public void EmitZeros(int numBytes)
        {
            _data.ZeroExtend(numBytes);
        }

        private Reservation GetReservationTicket(int size)
        {
#if DEBUG
            _numReservations++;
#endif
            Reservation ticket = (Reservation)_data.Count;
            _data.ZeroExtend(size);
            return ticket;
        }

        private int ReturnReservationTicket(Reservation reservation)
        {
#if DEBUG
            Debug.Assert(_numReservations > 0);
            _numReservations--;
#endif
            return (int)reservation;
        }

        public Reservation ReserveByte()
        {
            return GetReservationTicket(1);
        }

        public void EmitByte(Reservation reservation, byte emit)
        {
            int offset = ReturnReservationTicket(reservation);
            _data[offset] = emit;
        }

        public Reservation ReserveShort()
        {
            return GetReservationTicket(2);
        }

        public void EmitShort(Reservation reservation, short emit)
        {
            int offset = ReturnReservationTicket(reservation);
            _data[offset] = (byte)(emit & 0xFF);
            _data[offset + 1] = (byte)((emit >> 8) & 0xFF);
        }

        public Reservation ReserveInt()
        {
            return GetReservationTicket(4);
        }

        public void EmitInt(Reservation reservation, int emit)
        {
            int offset = ReturnReservationTicket(reservation);
            _data[offset] = (byte)(emit & 0xFF);
            _data[offset + 1] = (byte)((emit >> 8) & 0xFF);
            _data[offset + 2] = (byte)((emit >> 16) & 0xFF);
            _data[offset + 3] = (byte)((emit >> 24) & 0xFF);
        }

        public void EmitReloc(ISymbolNode symbol, RelocType relocType, int delta = 0)
        {
#if DEBUG
            if (_checkAllSymbolDependenciesMustBeMarked)
            {
                var node = symbol as ILCompiler.DependencyAnalysisFramework.DependencyNodeCore<NodeFactory>;
                if (node != null)
                    Debug.Assert(node.Marked);
            }
#endif

            _relocs.Add(new Relocation(relocType, _data.Count, symbol));

            // And add space for the reloc
            switch (relocType)
            {
                case RelocType.IMAGE_REL_BASED_REL32:
                case RelocType.IMAGE_REL_BASED_RELPTR32:
                case RelocType.IMAGE_REL_BASED_ABSOLUTE:
                case RelocType.IMAGE_REL_BASED_HIGHLOW:
                case RelocType.IMAGE_REL_SECREL:
                case RelocType.IMAGE_REL_BASED_ADDR32NB:
                    EmitInt(delta);
                    break;
                case RelocType.IMAGE_REL_BASED_DIR64:
                    EmitLong(delta);
                    break;
                case RelocType.IMAGE_REL_THUMB_BRANCH24:
                case RelocType.IMAGE_REL_THUMB_MOV32:
                    // Do not vacate space for this kind of relocation, because
                    // the space is embedded in the instruction.
                    break;                    
                default:
                    throw new NotImplementedException();
            }
        }

        public void EmitPointerReloc(ISymbolNode symbol, int delta = 0)
        {
            EmitReloc(symbol, (_target.PointerSize == 8) ? RelocType.IMAGE_REL_BASED_DIR64 : RelocType.IMAGE_REL_BASED_HIGHLOW, delta);
        }

        /// <summary>
        /// Use this api to generate a reloc to a symbol that may be an indirection cell or not as a pointer
        /// </summary>
        /// <param name="symbol">symbol to reference</param>
        /// <param name="indirectionBit">value to OR in to the reloc to represent to runtime code that this pointer is an indirection. Defaults to IndirectionConstants.IndirectionCellPointer</param>
        /// <param name="delta">Delta from symbol start for value</param>
        public void EmitPointerRelocOrIndirectionReference(ISymbolNode symbol, int indirectionBit = IndirectionConstants.IndirectionCellPointer, int delta = 0)
        {
            if (symbol.RepresentsIndirectionCell)
                delta |= indirectionBit;

            EmitReloc(symbol, (_target.PointerSize == 8) ? RelocType.IMAGE_REL_BASED_DIR64 : RelocType.IMAGE_REL_BASED_HIGHLOW, delta);
        }

        public ObjectNode.ObjectData ToObjectData()
        {
#if DEBUG
            Debug.Assert(_numReservations == 0);
#endif

            ObjectNode.ObjectData returnData = new ObjectNode.ObjectData(_data.ToArray(),
                                                                         _relocs.ToArray(),
                                                                         Alignment,
                                                                         _definedSymbols.ToArray());

            return returnData;
        }

        public enum Reservation { }

        public void AddSymbol(ISymbolDefinitionNode node)
        {
            _definedSymbols.Add(node);
        }
    }
}
