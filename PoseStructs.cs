using System;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Actors.Types;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
// ReSharper disable FieldCanBeMadeReadOnly.Local

namespace PoseTest
{
    public static class PoseStructs
    {
        public const int ActorDrawObjectOffset = 240;
        public const int DrawObjectSkeletonOffset = 160;

        [StructLayout(LayoutKind.Explicit)]
        public unsafe struct RenderSkeleton
        {
            [FieldOffset(80)] public short PartialSkeletonNum;
            [FieldOffset(104)] public PartialSkeleton* PartialSkeletons;
            
            public static RenderSkeleton* FromActor(Actor p)
            {
                if (p.Address == IntPtr.Zero) return null;
                var drawObject = Marshal.ReadIntPtr(p.Address, ActorDrawObjectOffset);
                if (drawObject == IntPtr.Zero) return null;
                var renderSkele = Marshal.ReadIntPtr(drawObject, DrawObjectSkeletonOffset);
                if (renderSkele == IntPtr.Zero) return null;
                return (RenderSkeleton*) renderSkele.ToPointer();
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 448)]
        public unsafe struct PartialSkeleton
        {
            [FieldOffset(300)] public short ConnectedBoneIndex;
            [FieldOffset(302)] public short ConnectedParentBoneIndex;

            [FieldOffset(320)] public hkaPose* Pose1;
            [FieldOffset(328)] public hkaPose* Pose2;
            [FieldOffset(336)] public hkaPose* Pose3;
            [FieldOffset(344)] public hkaPose* Pose4;

            [FieldOffset(352)] public RenderSkeleton* RenderSkeletonPtr;

            public override string ToString()
            {
                return $@"ConnectedBoneIndex: {ConnectedBoneIndex}, ConnectedParentBoneIndex: {ConnectedParentBoneIndex},
                            Pose1 @ {(ulong) Pose1:X}, Pose2 @ {(ulong) Pose2:X},
                            Pose3 @ {(ulong) Pose3:X}, Pose4 @ {(ulong) Pose4:X},
                            RenderSkeleton @ {(ulong) RenderSkeletonPtr:X}";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct hkaSkeleton
        {
            private void* VfPtr;
            private ulong Unknown;

            public char* Name;

            public hkaArray<short> ParentIndices;
            public hkaArray<hkaBone> Bones;
            public hkaArray<hkQsTransform> ReferencePose;

            public string GetName()
            {
                return Marshal.PtrToStringAnsi(new IntPtr(Name));
            }

            public override string ToString()
            {
                return GetName();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct hkaBone
        {
            public byte* NamePtr;
            public bool LockTranslation;

            public string GetName()
            {
                return Marshal.PtrToStringAnsi(new IntPtr(NamePtr));
            }

            public override string ToString()
            {
                return GetName();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct hkQsTransform
        {
            public hkVector4 Translation;
            public hkVector4 Rotation;
            public hkVector4 Scale;

            public override string ToString()
            {
                return $"({Translation}), ({Rotation}), ({Scale})";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct hkVector4
        {
            public float x;
            public float y;
            public float z;
            public float w;

            public override string ToString()
            {
                return $"({x}, {y}, {z}, {w})";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct hkaPose
        {
            public hkaSkeleton* SkeletonPointer;

            public hkaArray<hkQsTransform> LocalPose;
            public hkaArray<hkQsTransform> ModelPose; //this is matrix use
            public hkaArray<uint> BoneFlags; //unused, but we're sequential
            public bool ModelInSync;
            public bool LocalInSync;
            public int NumBones;

            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();
                sb.Append($"hkaPose skl ({SkeletonPointer->ToString()} @ {(ulong) SkeletonPointer:X}) w/ {BoneFlags.Length} bones, mis: {ModelInSync} lis: {LocalInSync}\n");
                // for (int i = 0; i < LocalPose.Length; i += 4)
                //     sb.Append($"{LocalPose[i]} {LocalPose[i + 1]} {LocalPose[i + 2]} {LocalPose[i + 3]}\n");
                return sb.ToString();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct hkaArray<T> where T : unmanaged
        {
            public T* Data;
            public int Length;
            private int CapacityAndFlags;
        
            public T this[int index]
            {
                get => Data[index];
                set => Data[index] = value;
            }
        }
    }
}