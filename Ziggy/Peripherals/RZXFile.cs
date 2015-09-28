﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using zlib;

namespace Peripherals
{
    public enum RZX_State {
        NONE,
        PLAYBACK,
        RECORDING
    }

    public enum RZX_BlockType {
        CREATOR = 0x10,
        SECURITY_INFO = 0x20,
        SECURITY_SIG = 0x21,
        SNAPSHOT = 0x30,
        RECORD = 0x80,
    }


    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RZX_Header {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public char[] signature;

        public byte majorVersion;
        public byte minorVersion;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RZX_Block {
        public byte id;
        public uint size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RZX_Creator {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public char[] author;

        public ushort majorVersion;
        public ushort minorVersion;
        //custom data of adjusted block size bytes follows
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RZX_Snapshot {
        public uint flags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public char[] extension;

        public uint uncompressedSize;
        //snapshot data/descriptor of adjusted block size bytes follows
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RZX_SnapshotDescriptor {
        public uint checksum;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RZX_Record {
        public uint numFrames;
        public byte reserved;
        public uint tstatesAtStart;
        public uint flags;
        //sequence of frames follows
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RZX_Frame {
        public ushort instructionCount;
        public ushort inputCount;
        public byte[] inputs;
    }

    public class RZXInfo {
        public RZX_Header header;
        public RZX_Creator creator;
        public List<RZX_Block> blocks;

        public override string ToString() {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(255);
            sb.Append(header.signature);
            sb.Append(" " + header.majorVersion + "." + header.minorVersion + "\nCreated by ");
            sb.Append(new String(creator.author, 0 , creator.author.Length - 1));
            sb.Append( creator.majorVersion + "." + creator.minorVersion);
            sb.Append("\nBlocks:\n");

            foreach(RZX_Block block in blocks) {
                sb.Append("ID: " + block.id);
                sb.Append(", Length: " + block.size);
                sb.Append("\n");
            }
            return sb.ToString();
        }
    }

    public class RZXSnapshotData {
        public String extension;
        public byte[] data;
    }

    public class RZXFileEventArgs {
        public RZX_BlockType blockID;
        public RZXInfo info;
        public RZXSnapshotData snapData;
        public uint tstates;
    }

    public class RZXFile {
        public System.Action<RZXFileEventArgs> RZXFileEventHandler;
        public RZX_Header header;
        public RZX_Creator creator;
        public RZX_Record record;
        public RZX_Snapshot snap;
        public char[][] snapshotExtension = new char[2][];
        public byte[][] snapshotData = new byte[2][];

        private bool isCompressedFrames = true;
        private BinaryWriter binaryWriter;
        private BinaryReader binaryReader;
        private uint tstatesAtRecordStart = 0;
        private uint frameDataSize = 0;
        public int frameCount = 0;
        private RZX_State state = RZX_State.NONE;
        private bool isRecordingBlock = false;
        private FileStream rzxFile;
        private byte snapIndex = 0;
        private long bookmarkFilePos;
        private long currentRecordFilePos;
        
        private const string rzxSessionContinue = "Zero RZX Continue\0";
        private const string rzxSessionFinal = "Zero RZX Final   \0";

        //RZX Playback & Recording
        private class RollbackBookmark {
            public SZXFile snapshot;
            public uint tstates;
        };

        private List<byte> inputs = new List<byte>();
        private List<byte> oldInputs = new List<byte>();
        private const int ZBUFLEN = 16384;
        private byte[] zBuffer;
        private byte[] fileBuffer;
        private ZStream zStream;
        private GCHandle pinnedBuffer;
        private bool isReading = false;
        private bool isReadingIRB = false;
        private int readBlockIndex = 0;
        public int fetchCount;
        public int inputCount;

        //Used for rollbacks
        private long continueSnapFilePos;
        private long lastRecordFilePos;
        private int currentBookmark = 0;
        private List<RollbackBookmark> bookmarks = new List<RollbackBookmark>();

        public RZX_Frame frame;
        public List<RZX_Frame> frames = new List<RZX_Frame>();

        #region v1
        public bool LoadRZX(Stream fs) {
            using (BinaryReader r = new BinaryReader(fs, System.Text.Encoding.UTF8)) {
                int bytesToRead = (int)fs.Length;

                byte[] buffer = new byte[bytesToRead];
                int bytesRead = r.Read(buffer, 0, bytesToRead);

                if (bytesRead == 0)
                    return false; //something bad happened!

                GCHandle pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);

                //Read in the szx header to begin proceedings
                header = (RZX_Header)Marshal.PtrToStructure(pinnedBuffer.AddrOfPinnedObject(),
                                                                         typeof(RZX_Header));

                String sign = new String(header.signature);
                if (sign != "RZX!") {
                    pinnedBuffer.Free();
                    return false;
                }

                int bufferCounter = Marshal.SizeOf(header);

                while (bufferCounter < bytesRead) {
                    //Read the block info
                    RZX_Block block = (RZX_Block)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Block));

                    bufferCounter += Marshal.SizeOf(block);
                    switch (block.id) {
                        case 0x10:
                            creator = (RZX_Creator)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Creator));
                            break;

                        case 0x30:
                            snap = (RZX_Snapshot)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Snapshot));

                            int offset = bufferCounter + Marshal.SizeOf(snap);

                            if ((snap.flags & 0x2) != 0) {
                                int snapSize = (int)block.size - Marshal.SizeOf(snap) - Marshal.SizeOf(block);
                                MemoryStream compressedData = new MemoryStream(buffer, offset, snapSize);
                                MemoryStream uncompressedData = new MemoryStream();

                                using (ZInputStream zipStream = new ZInputStream(compressedData)) {
                                    byte[] tempBuffer = new byte[2048];
                                    int bytesUnzipped = 0;

                                    while ((bytesUnzipped = zipStream.read(tempBuffer, 0, 2048)) > 0) {
                                        uncompressedData.Write(tempBuffer, 0, bytesUnzipped);
                                    }
                                    snapshotData[snapIndex] = uncompressedData.ToArray();
                                    compressedData.Close();
                                    uncompressedData.Close();
                                }
                            }
                            else {
                                snapshotData[snapIndex] = new byte[snap.uncompressedSize];
                                Array.Copy(buffer, offset, snapshotData[snapIndex], 0, snap.uncompressedSize);
                            }

                            snapshotExtension[snapIndex] = snap.extension;
                            snapIndex += (byte)(snapIndex < 1 ? 1 : 0);
                            break;

                        case 0x80:
                            record = (RZX_Record)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Record));

                            int offset2 = bufferCounter + Marshal.SizeOf(record);
                            byte[] frameBuffer;
                            if ((record.flags & 0x2) != 0) {
                                int frameSize = (int)block.size - Marshal.SizeOf(record) - Marshal.SizeOf(block);
                                MemoryStream compressedData = new MemoryStream(buffer, offset2, frameSize);
                                MemoryStream uncompressedData = new MemoryStream();
                                using (ZInputStream zipStream = new ZInputStream(compressedData)) {
                                    byte[] tempBuffer = new byte[2048];
                                    int bytesUnzipped = 0;
                                    while ((bytesUnzipped = zipStream.read(tempBuffer, 0, 2048)) > 0) {
                                        uncompressedData.Write(tempBuffer, 0, bytesUnzipped);
                                    }
                                    frameBuffer = uncompressedData.ToArray();
                                    compressedData.Close();
                                    uncompressedData.Close();
                                }
                            } else //All frame data is supposed to be compressed, but just in case...
                            {
                                int frameSize = (int)block.size - Marshal.SizeOf(record) - Marshal.SizeOf(block);
                                frameBuffer = new byte[frameSize];
                                Array.Copy(buffer, offset2, frameBuffer, 0, frameSize);
                            }

                            offset2 = 0;
                            for (int f = 0; f < record.numFrames; f++) {
                                RZX_Frame frame = new RZX_Frame();
                                try {
                                    frame.instructionCount = BitConverter.ToUInt16(frameBuffer, offset2);
                                    offset2 += 2;
                                    frame.inputCount = BitConverter.ToUInt16(frameBuffer, offset2);
                                    offset2 += 2;
                                    if ((frame.inputCount == 65535)) {
                                        frame.inputCount = frames[frames.Count - 1].inputCount;
                                        frame.inputs = new byte[frame.inputCount];
                                        if (frame.inputCount > 0)
                                            Array.Copy(frames[frames.Count - 1].inputs, 0, frame.inputs, 0, frame.inputCount);
                                    } else if (frame.inputCount > 0) {
                                        frame.inputs = new byte[frame.inputCount];
                                        Array.Copy(frameBuffer, offset2, frame.inputs, 0, frame.inputCount);
                                        offset2 += frame.inputCount;
                                    } else {
                                        frame.inputs = new byte[0];
                                    }
                                    frames.Add(frame);
                                } catch (Exception e) {
                                    return false;
                                }
                            }

                            break;

                        default: //unrecognised block, so skip to next
                            break;
                    }
                    bufferCounter += (int)block.size - Marshal.SizeOf(block); //Move to next block
                }
                pinnedBuffer.Free();
            }
            return true;
        }

        public bool LoadRZX(string filename) {
            bool readSZX;
            using (FileStream fs = new FileStream(filename, FileMode.Open)) {
                readSZX = LoadRZX(fs);
            }
            return readSZX;
        }

        public static void CopyStream(System.IO.Stream input, System.IO.Stream output) {
            byte[] buffer = new byte[2000];
            int len;
            while ((len = input.Read(buffer, 0, 2000)) > 0) {
                output.Write(buffer, 0, len);
            }
            output.Flush();
        }

        public void SaveRZX(string filename) {
            header = new RZX_Header();
            header.majorVersion = 0;
            header.minorVersion = 12;
            header.flags = 0;
            header.signature = "RZX!".ToCharArray();

            creator = new RZX_Creator();
            creator.author = "Zero Emulator      \0".ToCharArray();
            creator.majorVersion = 6;
            creator.minorVersion = 0;

            using (FileStream fs = new FileStream(filename, FileMode.Create)) {
                using (BinaryWriter r = new BinaryWriter(fs)) {
                    byte[] buf;
                    buf = RawSerialize(header); //header is filled in by the callee machine
                    r.Write(buf);

                    RZX_Block block = new RZX_Block();
                    block.id = 0x10;
                    block.size = (uint)Marshal.SizeOf(creator) + 5;
                    buf = RawSerialize(block);
                    r.Write(buf);
                    buf = RawSerialize(creator);
                    r.Write(buf);

                    for (int f = snapshotData.Length - 1; f >= 0; f--) {

                        if (snapshotData[f] == null)
                            continue;

                        snap = new RZX_Snapshot();
                        snap.extension = "szx\0".ToCharArray();
                        snap.flags |= 0x2;
                        byte[] rawSZXData;
                        snap.uncompressedSize = (uint)snapshotData[f].Length;

                        using (MemoryStream outMemoryStream = new MemoryStream())
                        using (ZOutputStream outZStream = new ZOutputStream(outMemoryStream, zlibConst.Z_DEFAULT_COMPRESSION))
                        using (Stream inMemoryStream = new MemoryStream(snapshotData[f])) {
                            CopyStream(inMemoryStream, outZStream);
                            outZStream.finish();
                            rawSZXData = outMemoryStream.ToArray();
                        }

                        block.id = 0x30;
                        block.size = (uint)Marshal.SizeOf(snap) + (uint)rawSZXData.Length + 5;
                        buf = RawSerialize(block);
                        r.Write(buf);
                        buf = RawSerialize(snap);
                        r.Write(buf);
                        r.Write(rawSZXData);
                    }

                    record.numFrames = (uint)frames.Count;
                    block.id = 0x80;
                    byte[] rawFramesData;
                    using (MemoryStream outMemoryStream = new MemoryStream())
                    using (ZOutputStream outZStream = new ZOutputStream(outMemoryStream, zlibConst.Z_DEFAULT_COMPRESSION)) {
                        foreach (RZX_Frame frame in frames) {
                            using (Stream inMemoryStream = new MemoryStream()) {
                                BinaryWriter bw = new BinaryWriter(inMemoryStream);
                                bw.Write(frame.instructionCount);
                                bw.Write(frame.inputCount);
                                bw.Write(frame.inputs);
                                bw.Seek(0, 0);
                                CopyStream(inMemoryStream, outZStream);
                                bw.Close();
                            }
                        }

                        outZStream.finish();
                        rawFramesData = outMemoryStream.ToArray();
                    }

                    block.size = (uint)Marshal.SizeOf(record) + (uint)rawFramesData.Length + 5;
                    buf = RawSerialize(block);
                    r.Write(buf);
                    buf = RawSerialize(record);
                    r.Write(buf);
                    r.Write(rawFramesData);
                }
            }
        }

        private static byte[] RawSerialize(object anything) {
            int rawsize = Marshal.SizeOf(anything);
            IntPtr buffer = Marshal.AllocHGlobal(rawsize);
            Marshal.StructureToPtr(anything, buffer, false);
            byte[] rawdatas = new byte[rawsize];
            Marshal.Copy(buffer, rawdatas, 0, rawsize);
            Marshal.FreeHGlobal(buffer);
            return rawdatas;
        }

        public void InitPlayback() {
            frameCount = 0;
            fetchCount = 0;
            inputCount = 0;
            frame = frames[0];
        }

        public void ContinueRecording() {
            snapshotData[0] = snapshotData[1];
            inputs = new List<byte>();
            frameCount = 0;
            fetchCount = 0;
            inputCount = 0;
        }
/*
        public void InsertBookmark(SZXFile szx, List<byte> inputList) {
            frame = new RZX_Frame();
            frame.inputCount = (ushort)inputList.Count;
            frame.instructionCount = (ushort)fetchCount;
            frame.inputs = inputList.ToArray();
            frames.Add(frame);
            fetchCount = 0;
            inputCount = 0;

            RollbackBookmark bookmark = new RollbackBookmark();
            bookmark.frameIndex = frames.Count;
            bookmark.snapshot = szx;
            bookmarks.Add(bookmark);
            currentBookmark = bookmarks.Count - 1;
        }

        public SZXFile Rollback() {
            if (bookmarks.Count > 0) {
                RollbackBookmark bookmark = bookmarks[currentBookmark];
                //if less than 2 seconds have passed since last bookmark, revert to an even earlier bookmark
                if ((frames.Count - bookmark.frameIndex) / 50 < 2) {
                    if (currentBookmark > 0) {
                        bookmarks.Remove(bookmark);
                        currentBookmark--;
                    }
                    bookmark = bookmarks[currentBookmark];
                }
                frames.RemoveRange(bookmark.frameIndex, frames.Count - bookmark.frameIndex);
                fetchCount = 0;
                inputCount = 0;
                inputs = new List<byte>();
                return bookmark.snapshot;
            }
            return null;
        }
*/
        public void Discard() {
            bookmarks.Clear();
            inputs.Clear();
        }

        public void Save(string fileName, byte[] data) {
            snapshotData[1] = data;
            bookmarks.Clear();
            SaveRZX(fileName);
        }

        public void StartRecording(byte[] data, int totalTStates) {
            record.tstatesAtStart = (uint)totalTStates;
            record.flags |= 0x2; //Frames are compressed.
            snapshotData[0] = data;
        }

        public bool IsValidSession(string filename) {
            //if (snapshotData[1] == null)
            //    return false;

            if (!OpenFile(filename))
                return false;

            List<RZX_Block> blocks = Scan();

            string c = new string(creator.author);

            if (!c.Contains("Zero"))
                return false;

            int snapCount = 0;
            
            for (int i = 0; i < blocks.Count; i++) {
                if (blocks[i].id == (int)RZX_BlockType.SNAPSHOT)
                    snapCount++;
            }

            if (snapCount < 2)
                return false;

            return true;
        }

        public bool NextPlaybackFrame() {
            frameCount++;
            fetchCount = 0;
            inputCount = 0;

            if (frameCount < frames.Count) {
                frame = frames[frameCount];
                return true;
            }

            return false;
        }

        public void RecordFrame(List<byte> inputList) {
            frame = new RZX_Frame();
            frame.inputCount = (ushort)inputList.Count;
            frame.instructionCount = (ushort)fetchCount;
            frame.inputs = inputList.ToArray();
            frames.Add(frame);
            fetchCount = 0;
            inputCount = 0;
        }

        public int GetPlaybackPercentage() {
            return frameCount * 100 / frames.Count;
        }
        #endregion

        public void Bookmark(SZXFile szx) {
            if (!isReading && isRecordingBlock) {
                CloseIRB();

                if (bookmarks.Count == 0)
                    continueSnapFilePos = rzxFile.Position;

                RollbackBookmark bookmark = new RollbackBookmark();
                bookmark.snapshot = szx;
                bookmark.tstates = tstatesAtRecordStart;
                bookmarks.Add(bookmark);
                currentBookmark = bookmarks.Count - 1;

                rzxFile.Seek(continueSnapFilePos, SeekOrigin.Begin);
                AddSnapshot(szx.GetSZXData());
                rzxFile.Seek(currentRecordFilePos, SeekOrigin.Begin);
            }
        }

        public void Rollback() {
            if (!isReading && isRecordingBlock && bookmarks.Count > 0) {
                RollbackBookmark bookmark = bookmarks[currentBookmark];

                //if less than 2 seconds have passed since last bookmark, revert to an even earlier bookmark
                if (frameCount < 25) {
                    if (currentBookmark > 0) {
                        bookmarks.Remove(bookmark);
                        currentBookmark--;
                    }
                    bookmark = bookmarks[currentBookmark];
                }

                //The current record block is invalid now, so we save it out but we will set it up to be overwritten by subsequent file writes
                long purgeRecordFilePos = lastRecordFilePos;
                CloseIRB();
                lastRecordFilePos = purgeRecordFilePos;

                RZXFileEventArgs arg = new RZXFileEventArgs();
                arg.blockID = RZX_BlockType.SNAPSHOT;
                arg.tstates = bookmark.tstates;
                arg.snapData = new RZXSnapshotData();
                arg.snapData.extension = "szx\0";
                arg.snapData.data = bookmark.snapshot.GetSZXData();

                if (RZXFileEventHandler != null)
                    RZXFileEventHandler(arg);
            }
        }

        public bool Record(string filename) {
            header = new RZX_Header();
            header.majorVersion = 0;
            header.minorVersion = 12;
            header.flags = 0;
            header.signature = "RZX!".ToCharArray();

            creator = new RZX_Creator();
            creator.author = "Zero Emulator      \0".ToCharArray();
            creator.majorVersion = 6;
            creator.minorVersion = 0;

            frameCount = 0;
            fetchCount = 0;
            inputCount = 0;
                
            try {
                rzxFile = new FileStream(filename, FileMode.Create);
                binaryWriter = new BinaryWriter(rzxFile);
                byte[] buf;
                buf = RawSerialize(header);
                binaryWriter.Write(buf);

                RZX_Block block = new RZX_Block();
                block.id = 0x10;
                block.size = (uint)Marshal.SizeOf(creator) + 5;
                buf = RawSerialize(block);
                binaryWriter.Write(buf);
                buf = RawSerialize(creator);
                binaryWriter.Write(buf);
                state = RZX_State.RECORDING;
            }
            catch {
                return false;
            }
  
            return true;
        }

        private bool OpenFile(string filename) {
            rzxFile = new FileStream(filename, FileMode.Open);
            binaryReader = new BinaryReader(rzxFile);
            int bytesToRead = (int)rzxFile.Length;

            if (bytesToRead == 0)
                return false; //something bad happened!

            fileBuffer = new byte[bytesToRead];
            binaryReader.Read(fileBuffer, 0, 10);

            pinnedBuffer = GCHandle.Alloc(fileBuffer, GCHandleType.Pinned);

            header = (RZX_Header)Marshal.PtrToStructure(pinnedBuffer.AddrOfPinnedObject(),
                                                                     typeof(RZX_Header));

            String sign = new String(header.signature);

            if (sign != "RZX!") {
                pinnedBuffer.Free();
                return false;
            }

            return true;
        }

        public List<RZX_Block> Scan() {
            RZXFileEventArgs rzxArgs = new RZXFileEventArgs();
            rzxArgs.info = new RZXInfo();
            rzxArgs.info.header = header;
            rzxArgs.info.blocks = new List<RZX_Block>();

            while (binaryReader.BaseStream.Position != binaryReader.BaseStream.Length) {
                if (binaryReader.Read(fileBuffer, 0, 5) < 1)
                    break;

                RZX_Block block = (RZX_Block)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(fileBuffer, 0), typeof(RZX_Block));
                rzxArgs.info.blocks.Add(block);

                binaryReader.Read(fileBuffer, 0, (int)block.size - Marshal.SizeOf(block));

                switch (block.id) {
                    case (int)RZX_BlockType.CREATOR:
                        creator = (RZX_Creator)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(fileBuffer, 0),
                                                                     typeof(RZX_Creator));
                        rzxArgs.info.creator = creator;
                        rzxArgs.blockID = RZX_BlockType.CREATOR;
                        break;

                    default:
                        break;
                }
            }

            if (RZXFileEventHandler != null)
                RZXFileEventHandler(rzxArgs);

            rzxFile.Seek(10, SeekOrigin.Begin);
            readBlockIndex = 10;
            return rzxArgs.info.blocks;
        }

        public bool Playback(string filename) {
            if (!OpenFile(filename))
                return false;

            Scan();
            isReading = true;

            if (!SeekIRB())
                return false;

            state = RZX_State.PLAYBACK;
            fetchCount = 0;
            frame = new RZX_Frame();
            frame.inputCount = 0xffff;
            return true;
        }

        private bool SeekIRB() {

            while (binaryReader.BaseStream.Position != binaryReader.BaseStream.Length) {

                //Read in the block header
                if (binaryReader.Read(fileBuffer, 0, 5) < 1)
                    return false;

                RZX_Block block = (RZX_Block)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(fileBuffer, 0), typeof(RZX_Block));
                int blockSize = Marshal.SizeOf(block);
                int blockDataSize = (int)block.size - blockSize;

                readBlockIndex += blockSize;

                switch (block.id) {
                    case (int)RZX_BlockType.SNAPSHOT:
                        {
                            //Read in the block data
                            binaryReader.Read(fileBuffer, 0, blockDataSize);

                            RZXFileEventArgs rzxArgs = new RZXFileEventArgs();
                            rzxArgs.blockID = RZX_BlockType.SNAPSHOT;
                            rzxArgs.snapData = new RZXSnapshotData();

                            snap = (RZX_Snapshot)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(fileBuffer, 0),
                                                                         typeof(RZX_Snapshot));

                            int snapDataOffset = Marshal.SizeOf(snap);

                            if ((snap.flags & 0x2) != 0) {
                                int snapSize = blockDataSize - snapDataOffset;

                                MemoryStream compressedData = new MemoryStream(fileBuffer, snapDataOffset, snapSize);
                                MemoryStream uncompressedData = new MemoryStream();

                                using (ZInputStream zipStream = new ZInputStream(compressedData)) {
                                    byte[] tempBuffer = new byte[2048];
                                    int bytesUnzipped = 0;

                                    while ((bytesUnzipped = zipStream.read(tempBuffer, 0, 2048)) > 0)
                                        uncompressedData.Write(tempBuffer, 0, bytesUnzipped);

                                    rzxArgs.snapData.data = uncompressedData.ToArray();
                                    compressedData.Close();
                                    uncompressedData.Close();
                                }
                            }
                            else {
                                rzxArgs.snapData.data = new byte[snap.uncompressedSize];
                                Array.Copy(fileBuffer, snapDataOffset, rzxArgs.snapData.data, 0, snap.uncompressedSize);
                            }

                            rzxArgs.snapData.extension = new String(snap.extension).ToLower();

                            if (RZXFileEventHandler != null)
                                RZXFileEventHandler(rzxArgs);

                            readBlockIndex += blockDataSize;
                        }
                        return true;

                    case (int)RZX_BlockType.RECORD:
                        {
                            int recordSize = Marshal.SizeOf(new RZX_Record());
                            binaryReader.Read(fileBuffer, 0, recordSize);

                            record = (RZX_Record)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(fileBuffer, 0),
                                                                         typeof(RZX_Record));
                            frameCount = (int)record.numFrames;
                            isReadingIRB = true;

                            RZXFileEventArgs rzxArgs = new RZXFileEventArgs();
                            rzxArgs.blockID = RZX_BlockType.RECORD;
                            rzxArgs.tstates = record.tstatesAtStart;
                            readBlockIndex += blockDataSize;

                            if (RZXFileEventHandler != null)
                                RZXFileEventHandler(rzxArgs);

                            if (isCompressedFrames) {
                                currentRecordFilePos = rzxFile.Position;
                                OpenZStream(currentRecordFilePos, true);
                            }

                            readBlockIndex += blockDataSize;
                        }
                        return true;

                    default: //unrecognised block, so skip to next
                        binaryReader.Read(fileBuffer, 0, blockDataSize); //dummy read to advance file pointer
                        break;
                }

                readBlockIndex += blockDataSize; //Move to next block
            }
            return false;
        }

        private void WriteFrame(byte[] inputs, ushort inCount) {
            BinaryWriter bw = new BinaryWriter(rzxFile);
            bw.Write((ushort)fetchCount);
            bw.Write(inCount);

            frameDataSize += (uint)(2 + 2);

            if (inputs != null && inputs.Length > 0) {
                bw.Write(inputs);
                frameDataSize += (uint)(inputs.Length);
            }
        }

        private int ReadFromZStream (ref byte[] buffer, int numBytesToRead) {
            zStream.next_out = buffer;
            zStream.avail_out = numBytesToRead;
            zStream.next_out_index = 0;

            while (zStream.avail_out > 0) {

                if (zStream.avail_in == 0) {
                    zStream.avail_in = binaryReader.Read(zBuffer, 0, ZBUFLEN);

                    if (zStream.avail_in == 0)
                        return 0;

                    zStream.next_in = zBuffer;
                    zStream.next_in_index = 0;
                }

                zStream.inflate(zlibConst.Z_NO_FLUSH);
            }
            return numBytesToRead - zStream.avail_out;
        }

        private int WriteToZStream(byte[] buffer, int numBytesToWrite) {
            int err;
            zStream.avail_in = numBytesToWrite;
            zStream.next_in = buffer;
            zStream.next_in_index = 0;
            
            while (zStream.avail_in > 0) {

                if (zStream.avail_out == 0) {
                    binaryWriter.Write(zBuffer, 0, ZBUFLEN);
                    zStream.next_out = zBuffer;
                    zStream.next_out_index = 0;
                    zStream.avail_out = ZBUFLEN;
                }
                err = zStream.deflate(zlibConst.Z_NO_FLUSH);
            }

            return numBytesToWrite - zStream.avail_in;
        }

        private int CloseZStream() {
            int len, err;
            bool done = false;

            zStream.avail_in = 0;

            while (!isReading) {
                len = ZBUFLEN - zStream.avail_out;

                if (len > 0) {
                    binaryWriter.Write(zBuffer, 0, len);
                    zStream.next_out = zBuffer;
                    zStream.avail_out = ZBUFLEN;
                    zStream.next_out_index = 0;
                }

                if (done)
                    break;

                err = zStream.deflate(zlibConst.Z_FINISH);
                done = (zStream.avail_out > 0 || err == zlibConst.Z_STREAM_END);
            }

            zBuffer = null;
            return 0;
        }

        private bool OpenZStream(long offset, bool isRead) {
            int err;
            zBuffer = new byte[ZBUFLEN];
            zStream = new ZStream();

            if (isRead) {
                zStream.next_in = zBuffer;
                zStream.next_in_index = 0;
                zStream.avail_in = 0;
                err = zStream.inflateInit();
                isReading = true;
            }
            else {
                err = zStream.deflateInit(zlibConst.Z_DEFAULT_COMPRESSION);
                zStream.next_out = zBuffer;
                zStream.next_out_index = 0;
                isReading = false;
            }

            zStream.avail_out = ZBUFLEN;

            if (err != zlibConst.Z_OK)
                return false;

            rzxFile.Seek(offset, SeekOrigin.Begin);
            return true;
        }

        public bool UpdatePlayback() {
            if (state != RZX_State.PLAYBACK)
                return false;

            if (isReadingIRB && (fetchCount == 0))
                isReadingIRB = false;

            if (!isReadingIRB) {
                if (!SeekIRB()) {
                    Close();
                    state = RZX_State.NONE;
                    return false;
                }
            }

            if (isCompressedFrames) {
                byte[] buffer = new byte[4];
                ReadFromZStream(ref buffer, 4);
                RZX_Frame newFrame = new RZX_Frame();
                newFrame.instructionCount = BitConverter.ToUInt16(buffer, 0);
                newFrame.inputCount = BitConverter.ToUInt16(buffer, 2);

                if (newFrame.inputCount > 0 && (newFrame.inputCount != 0xffff)) {
                    frame = newFrame;
                    frame.inputs = new byte[frame.inputCount];
                    ReadFromZStream(ref frame.inputs, frame.inputCount);
                }
                else
                    frame.instructionCount = newFrame.instructionCount;
            }
            inputCount = 0;
            fetchCount = 0;
            frameCount--;

            return true;
        }

        public bool UpdateRecording(List<byte> inputList, int tstates) {
            if (state != RZX_State.RECORDING)
                return false;

            if (!isRecordingBlock) {
                currentRecordFilePos = rzxFile.Position;
                tstatesAtRecordStart = (uint)tstates;

                record = new RZX_Record();
                record.numFrames = (uint)frameCount;           //This will be adjusted later when closing the record

                if (isCompressedFrames)
                    record.flags |= 0x2;

                record.tstatesAtStart = tstatesAtRecordStart;

                RZX_Block block = new RZX_Block();
                block.id = 0x80;
                block.size = (uint)Marshal.SizeOf(record) + 5; //This will be adjusted later when closing the record
                byte[] buf;
                buf = RawSerialize(block);

                binaryWriter.Write(buf);
                buf = RawSerialize(record);
                binaryWriter.Write(buf);

                isRecordingBlock = true;
                frameCount = 0;

                if (isCompressedFrames) 
                    OpenZStream(rzxFile.Position, false);
            }

            ushort inCount = 65535;
                    
            if (oldInputs.Count == inputList.Count) {

                for (int i = 0; i < inputList.Count; i++) {
                    if (inputList[i] != oldInputs[i]) { 
                        inCount = (ushort)inputList.Count;
                        break;
                    }
                }
            }
            else
                inCount = (ushort)inputList.Count;

            byte[] frameHeader = new byte[4];
            frameHeader[0] = (byte)(fetchCount & 0xff);
            frameHeader[1] = (byte)((fetchCount & 0xff00) >> 8);
            frameHeader[2] = (byte)(inCount & 0xff);
            frameHeader[3] = (byte)((inCount & 0xff00) >> 8);

            if (isCompressedFrames)
                WriteToZStream(frameHeader, 4);

            if ((inCount > 0) && (inCount != 65535))
                WriteToZStream(inputList.ToArray(), inputList.Count);

            fetchCount = 0;
            oldInputs.Clear();
            oldInputs = new List<byte>(inputList);
            frameCount++;

            return true;
        }

        private void CloseIRB() {
            if (isCompressedFrames)
                CloseZStream();

            zStream.deflateEnd();

            if (frameCount == 0) {
                rzxFile.Seek(currentRecordFilePos, SeekOrigin.Begin);
                isRecordingBlock = false;
                return;
            }

            long currentPos = rzxFile.Position;
            long len = currentPos - currentRecordFilePos;
            rzxFile.Seek(currentRecordFilePos, SeekOrigin.Begin);

            record = new RZX_Record();
            record.numFrames = (uint)frameCount;

            if (isCompressedFrames)
                record.flags |= 0x2;

            record.tstatesAtStart = tstatesAtRecordStart;

            RZX_Block block = new RZX_Block();
            block.id = 0x80;
            block.size = (uint)len;
            byte[] buf;
            buf = RawSerialize(block);

            binaryWriter.Write(buf);
            buf = RawSerialize(record);
            binaryWriter.Write(buf);

            rzxFile.Seek(currentPos, SeekOrigin.Begin);
            lastRecordFilePos = currentRecordFilePos;
            currentRecordFilePos = currentPos;
            isRecordingBlock = false;

        }

        public void Close() {
            if (isRecordingBlock)
                CloseIRB();

            if (!isReading) {
                binaryWriter.Flush();
                binaryWriter.Close();
                binaryWriter = null;
            }
            else {
                pinnedBuffer.Free();
                binaryReader.Close();
                binaryReader = null;
            }

            rzxFile.Close();
            rzxFile = null;
        }

        public void AddSnapshot(byte[] snapshotData) {
            snap = new RZX_Snapshot();
            snap.extension = "szx\0".ToCharArray();
            snap.flags |= 0x2;
            byte[] rawSZXData;
            snap.uncompressedSize = (uint)snapshotData.Length;

            using (MemoryStream outMemoryStream = new MemoryStream())
                using (ZOutputStream outZStream = new ZOutputStream(outMemoryStream, zlibConst.Z_DEFAULT_COMPRESSION))
                    using (Stream inMemoryStream = new MemoryStream(snapshotData)) {
                        CopyStream(inMemoryStream, outZStream);
                        outZStream.finish();
                        rawSZXData = outMemoryStream.ToArray();
                    }

            RZX_Block block = new RZX_Block();
            block.id = 0x30;
            block.size = (uint)Marshal.SizeOf(snap) + (uint)rawSZXData.Length + 5;
            byte[] buf;
            buf = RawSerialize(block);

            binaryWriter.Write(buf);
            buf = RawSerialize(snap);
            binaryWriter.Write(buf);
            binaryWriter.Write(rawSZXData);
        }
    }
}