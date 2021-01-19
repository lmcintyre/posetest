using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using ImGuiNET;

namespace PoseTest
{
    public unsafe class Plugin : IDalamudPlugin
    {
        public string Name => "Pose Test";

        private const string commandName = "/posetest";

        public DalamudPluginInterface pi;
        public Configuration configuration;
        private PluginUI ui;

        private bool _uiVisible = true;
        private bool _updatePaused = false;
        private PoseStructs.RenderSkeleton* _lastRenderSkele;
        
        // private delegate IntPtr SetBoneXSpacePrototype(void* thisRenderSkeleton, ushort boneId, ref PoseStructs.hkQsTransform, bool enableSecondary, bool enablePropagate);
        private delegate ulong SetBoneXSpacePrototype(void* thisRenderSkeleton, ushort boneId, void* transform, bool enableSecondary, bool enablePropagate);
        private Hook<SetBoneXSpacePrototype> _setBoneXSpaceHook;
        
        private delegate PoseStructs.hkQsTransform* GetBoneModelSpacePrototype(ref PoseStructs.hkaPose pose, int boneIdx);
        private Hook<GetBoneModelSpacePrototype> _getBoneModelSpaceHook;

        // UI
        private int _selectedPartialSkeleIndex = -1;
        private int _selectedBoneIndex = -1;

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            pi = pluginInterface;
            
            configuration = pi.GetPluginConfig() as Configuration ?? new Configuration();
            configuration.Initialize(pi);
            ui = new PluginUI(configuration, this);
            
            pi.CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "A useful message to display in /xlhelp"
            });

            try
            {
                var setBoneXSpacePtr = pi.TargetModuleScanner.ScanText("48 8B C4 48 89 58 18 55 56 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 0F 29 70 B8 0F 29 78 A8 44 0F 29 40 ?? 44 0F 29 48 ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B B1 ?? ?? ?? ?? 0F B7 C2 66 89 54 24 ?? 48 8B D1 48 89 4C 24 ?? 45 0F B6 F1 0F B6 8C 24 ?? ?? ?? ?? 4D 8B E0 44 88 4C 24 ?? 4C 89 44 24");
                _setBoneXSpaceHook = new Hook<SetBoneXSpacePrototype>(setBoneXSpacePtr, (SetBoneXSpacePrototype) SetBoneXSpaceDetour);
                
                var getBoneModelSpacePtr = pi.TargetModuleScanner.ScanText("40 53 48 83 EC 10 4C 8B 49 28 4C 8B D1 4C 63 DA 49 8B DB");
                _getBoneModelSpaceHook = new Hook<GetBoneModelSpacePrototype>(getBoneModelSpacePtr, (GetBoneModelSpacePrototype) GetBoneModelSpaceDetour);
            }
            catch (KeyNotFoundException)
            {
                
            }
            PluginLog.Log($"PoseTest enabled!");
            
            pi.UiBuilder.DisableAutomaticUiHide = true;
            pi.UiBuilder.OnBuildUi += DrawUI;
            pi.UiBuilder.OnOpenConfigUi += (sender, args) => DrawConfigUI();
        }
        
        private PoseStructs.hkQsTransform* GetBoneModelSpaceDetour(ref PoseStructs.hkaPose pose, int boneIdx)
        {
            var boneTransformPtr = pose.ModelPose.Data;
            return boneTransformPtr + boneIdx;
        }

        // private IntPtr SetBoneXSpaceDetour(void* thisRenderSkeleton, ushort boneId, ref PoseStructs.hkQsTransform transform, bool enableSecondary, bool enablePropagate)
        private ulong SetBoneXSpaceDetour(void* thisRenderSkeleton, ushort boneId, void* transform, bool enableSecondary, bool enablePropagate)
        {
            // var boneTransformPtr = pose.ModelPose.Data;
            // return boneTransformPtr;// + boneIdx;
            // var ret = _setBoneXSpaceHook.Original(thisRenderSkeleton, boneId, ref transform, enableSecondary, enablePropagate);
            // var ret = _setBoneXSpaceHook.Original(thisRenderSkeleton, boneId, transform, enableSecondary, enablePropagate);
            // PluginLog.Log($"{(ulong) thisRenderSkeleton:X} {boneId} ");
            // PluginLog.Log($"{(ulong) thisRenderSkeleton:X} {boneId} {(ulong) transform:X} {enableSecondary} {enablePropagate} = {ret}");
            
            var ret = (ulong) boneId;
            return ret;
        }

        public void Dispose()
        {
            _setBoneXSpaceHook.Disable();
            _setBoneXSpaceHook.Dispose();
            _getBoneModelSpaceHook.Disable();
            _getBoneModelSpaceHook.Dispose();
            ui.Dispose();
            pi.CommandManager.RemoveHandler(commandName);
            pi.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            _uiVisible = true;
        }
        
        private void DrawConfigUI()
        {
            _uiVisible = true;
        }
        
        private unsafe void DrawUI()
        {
            if (!_uiVisible)
                return;

            ImGui.Begin("PoseTest", ref _uiVisible);

            if (ImGui.Checkbox("Pause all skeleton updates", ref _updatePaused))
            {
                if (_updatePaused)
                {
                    _setBoneXSpaceHook.Enable();
                    _getBoneModelSpaceHook.Enable();
                }
                else
                {
                    _setBoneXSpaceHook.Disable();
                    _getBoneModelSpaceHook.Disable();
                }
            }
            
            var localPlayer = pi.ClientState?.LocalPlayer;
            if (localPlayer == null)
            {
                ImGui.End();
                return;
            }
            var renderSkele = PoseStructs.RenderSkeleton.FromActor(localPlayer);
            if (renderSkele == null)
            {
                ImGui.End();
                return;
            }

            if (renderSkele != _lastRenderSkele)
            {
                _lastRenderSkele = renderSkele;
                _selectedPartialSkeleIndex = -1;
                _selectedBoneIndex = -1;
            }
            
            if (renderSkele->PartialSkeletons != null)
            {
                if (ImGui.Selectable("Deselect"))
                {
                    _selectedPartialSkeleIndex = -1;
                    _selectedBoneIndex = -1;
                }
                for (int i = 0; i < renderSkele->PartialSkeletonNum; i++)
                {
                    var pSkl = renderSkele->PartialSkeletons[i];
                    if (pSkl.Pose1 != null)
                    {
                        if (ImGui.Selectable(pSkl.Pose1->ToString()))
                        {
                            _selectedPartialSkeleIndex = i;
                            _selectedBoneIndex = -1;
                        }

                        if (ImGui.IsItemHovered())
                        {
                            DrawTooltip($"{renderSkele->PartialSkeletons[i]}");
                        }
                    }
                }    
            }
            ImGui.End();

            if (_selectedPartialSkeleIndex != -1)
            {
                var pose = renderSkele->PartialSkeletons[_selectedPartialSkeleIndex].Pose1;
                var selectedSkl = pose->SkeletonPointer;
                ImGui.SetNextWindowSize(new Vector2(200, 600), ImGuiCond.FirstUseEver);
                ImGui.Begin(selectedSkl->GetName());
                if (_selectedBoneIndex != -1)
                {
                    ImGui.Text($"Bone: {selectedSkl->Bones[_selectedBoneIndex]}");
                    var parentIndex = selectedSkl->ParentIndices[_selectedBoneIndex];
                    var boneParentName = parentIndex == -1 ? "" : selectedSkl->Bones[parentIndex].GetName();
                    ImGui.Text($"Bone parent: {boneParentName}");
                    ImGui.Separator();
                    var boneTransform = pose->ModelPose[_selectedBoneIndex];
                    if (ImGui.SliderFloat("X", ref boneTransform.Rotation.x, -1.5f, 1.5f, "%.2f"))
                        pose->ModelPose[_selectedBoneIndex] = boneTransform;
                    if (ImGui.SliderFloat("Y", ref boneTransform.Rotation.y, -1.5f, 1.5f, "%.2f"))
                        pose->ModelPose[_selectedBoneIndex] = boneTransform;
                    if (ImGui.SliderFloat("Z", ref boneTransform.Rotation.z, -1.5f, 1.5f, "%.2f"))
                        pose->ModelPose[_selectedBoneIndex] = boneTransform;
                    if (ImGui.SliderFloat("W", ref boneTransform.Rotation.w, -1.5f, 1.5f, "%.2f"))
                        pose->ModelPose[_selectedBoneIndex] = boneTransform;
                    ImGui.Separator();
                }

                ImGui.BeginChild("scrolling", new Vector2(0, -1));
                if (ImGui.Selectable("Deselect"))
                    _selectedBoneIndex = -1;
                for (int i = 0; i < selectedSkl->Bones.Length; i++)
                {
                    var boneName = selectedSkl->Bones[i].GetName();
                    if (ImGui.Selectable(boneName))
                        _selectedBoneIndex = i;
                }

                if (_selectedBoneIndex != -1)
                {
                    MouseoverBone(pose->ModelPose[_selectedBoneIndex].Translation, selectedSkl->Bones[_selectedBoneIndex].GetName());
                }

                ImGui.EndChild();

                ImGui.End();
            }
        }

        private void MouseoverBone(PoseStructs.hkVector4 translate, string name)
        {
            var lp = pi?.ClientState?.LocalPlayer;
            if (lp == null) return;

            var coords = new SharpDX.Vector3
            {
                X = lp.Position.X + translate.x,
                Y = lp.Position.Z + translate.y,
                Z = lp.Position.Y + translate.z
            };

            if (!pi.Framework.Gui.WorldToScreen(coords, out var pos)) return;
            
            // ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(1, 0, 1, 1));
            ImGui.SetNextWindowSize(new Vector2(120, 30), ImGuiCond.Always);
            ImGui.SetNextWindowPos(new Vector2(pos.X, pos.Y));
            ImGui.Begin("##fejwofije", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar);
            ImGui.Text(name);
            ImGui.End();
            // ImGui.PopStyleColor();
        }

        // private Quaternion FromEuler()
        // {
        //     
        // }
        //
        // private Vector3 ToEuler(SharpDX.Quaternion q)
        // {
        //     
        // }

        private void DrawTooltip(string fmt)
        {
            ImGui.BeginTooltip();
            ImGui.Text(fmt);
            ImGui.EndTooltip();
        }

        private unsafe void DrawSkeleTooltip(PoseStructs.hkaSkeleton* skl)
        {
            ImGui.BeginTooltip();
            // for (int i = 0; i < skl->Bones.Length; i++)
            //     ImGui.Text($"{skl->Bones[i]}");
            ImGui.Text($"{*skl}");
            ImGui.EndTooltip();
        }
    }
}
