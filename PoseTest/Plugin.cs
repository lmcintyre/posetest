using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Actors.Types;
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

        // private delegate IntPtr SetBoneXSpacePrototype(void* thisRenderSkeleton, ushort boneId, ref hkQsTransform, bool enableSecondary, bool enablePropagate);
        private delegate ulong SetBoneModelSpacePrototype(PartialSkeleton* partialSkeleton, ushort boneId, hkQsTransform* transform, bool enableSecondary, bool enablePropagate);
        private Hook<SetBoneModelSpacePrototype> _setBoneModelSpaceHook;

        private delegate hkQsTransform* GetBoneModelSpacePrototype(ref hkaPose pose, int boneIdx);
        private Hook<GetBoneModelSpacePrototype> _getBoneModelSpaceHook;

        // It's also possible that this is hkaPose::SyncModel
        private delegate void SyncAllPrototype(ref hkaPose pose);
        private Hook<SyncAllPrototype> _syncAllHook;
        
        private delegate hkaSkeleton* GetSkeletonPrototype(ref hkaPose pose);
        private Hook<GetSkeletonPrototype> _getSkeletonHook;

        private delegate void ExecuteSampleBlendJobPrototype(void* job);
        private Hook<ExecuteSampleBlendJobPrototype> _executeSampleBlendJobHook;

        // UI
        private bool _uiVisible = true;
        private bool _updatePaused = false;
        private int _lastActorId = -1;
        private int _selectedActorId = -1;
        private int _selectedPartialSkeleIndex = -1;
        private int _selectedBoneIndex = -1;

        private bool UnknownHookEnabled = false;
        private int update = 0;

        private Vector3 _currentRotations = Vector3.Zero;
        private hkQsTransform* _transformSpace = null;

        private ulong _lastSetBoneModelSpaceSkeletonAddress = 0;
        
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
                var setBoneModelSpacePtr = pi.TargetModuleScanner.ScanText("48 8B C4 48 89 58 18 55 56 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 0F 29 70 B8 0F 29 78 A8 44 0F 29 40 ?? 44 0F 29 48 ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B B1 ?? ?? ?? ?? 0F B7 C2 66 89 54 24 ?? 48 8B D1 48 89 4C 24 ?? 45 0F B6 F1 0F B6 8C 24 ?? ?? ?? ?? 4D 8B E0 44 88 4C 24 ?? 4C 89 44 24");
                _setBoneModelSpaceHook = new Hook<SetBoneModelSpacePrototype>(setBoneModelSpacePtr, (SetBoneModelSpacePrototype) SetBoneModelSpaceDetour);

                var getBoneModelSpacePtr = pi.TargetModuleScanner.ScanText("40 53 48 83 EC 10 4C 8B 49 28 4C 8B D1 4C 63 DA 49 8B DB");
                _getBoneModelSpaceHook = new Hook<GetBoneModelSpacePrototype>(getBoneModelSpacePtr, (GetBoneModelSpacePrototype) GetBoneModelSpaceDetour);

                var syncAllPtr = pi.TargetModuleScanner.ScanText("48 83 EC 18 80 79 38 00 0F 85 ?? ?? ?? ?? 48 8B 01 45 33 C9 48 89 5C 24 ?? 48 63 58 30 48 85 DB 0F 8E ?? ?? ?? ?? 45 8B D1");
                _syncAllHook = new Hook<SyncAllPrototype>(syncAllPtr, (SyncAllPrototype) SyncAllDetour);

                var getSkeletonPtr = pi.TargetModuleScanner.ScanText("48 8B 01 4C 8B C9 4C 8B 41 08 48 8B 50 38 8B 40 30 8D 0C 40 85 C9 74 1C 0F 1F 84 00");
                _getSkeletonHook = new Hook<GetSkeletonPrototype>(getSkeletonPtr, (GetSkeletonPrototype) GetSkeletonDetour);
                
                // ExecuteSampleBlendJob?
                var executeSampleBlendJobPtr = pi.TargetModuleScanner.ScanText("48 8B C4 55 57 48 8D 68 98 48 81 EC ?? ?? ?? ?? 66 83 79 ?? ?? 48 8B F9 0F 84 ?? ?? ?? ?? 8B 0D");
                _executeSampleBlendJobHook = new Hook<ExecuteSampleBlendJobPrototype>(executeSampleBlendJobPtr, (ExecuteSampleBlendJobPrototype) ExecuteSampleBlendJobDetour);
            }
            catch (KeyNotFoundException) { }

            PluginLog.Log($"PoseTest enabled!");

            pi.UiBuilder.DisableAutomaticUiHide = true;
            pi.UiBuilder.OnBuildUi += DrawUI;
            pi.UiBuilder.OnOpenConfigUi += (sender, args) => DrawConfigUI();
        }

        private void ExecuteSampleBlendJobDetour(void* job)
        {
            // Do nothing to avoid the local pose updating its (blended) animation
        }

        // private void* UnknownDetour(void* unknown)
        // {
        //     var ret = UnknownHook.Original(unknown);
        //     PluginLog.Log($"UnknownDetour({(ulong) unknown:X}) = {(ulong) ret:X}");
        //     return ret;
        // }
        
        private hkaSkeleton* GetSkeletonDetour(ref hkaPose pose)
        {
            // The original function will for some reason attempt to sync/modify local space
            // so we'll just return the skeleton address as expected
            var hkaSkl = pose.SkeletonPointer;
            return hkaSkl;
        }

        private void SyncAllDetour(ref hkaPose pose)
        {
            // Do nothing so the pose will not sync
            
            // PluginLog.Log($"UnknownDetour: {(ulong) alsoUnknown:X}");
            // UnknownHook.Original(alsoUnknown);
        }

        private hkQsTransform* GetBoneModelSpaceDetour(ref hkaPose pose, int boneIdx)
        {
            // The original function will attempt to sync poses between spaces
            // so we'll just return the bone's model space transform address as expected
            var boneTransformPtr = pose.ModelPose.Data;
            return boneTransformPtr + boneIdx;
        }

        // private IntPtr SetBoneXSpaceDetour(void* thisRenderSkeleton, ushort boneId, ref hkQsTransform transform, bool enableSecondary, bool enablePropagate)
        private ulong SetBoneModelSpaceDetour(PartialSkeleton* partialSkeletons, ushort boneId, hkQsTransform* transform, bool enableSecondary, bool enablePropagate)
        {
            _lastSetBoneModelSpaceSkeletonAddress = (ulong) partialSkeletons;
            // var boneTransformPtr = pose.ModelPose.Data;
            // return boneTransformPtr;// + boneIdx;
            // var ret = _setBoneXSpaceHook.Original(thisRenderSkeleton, boneId, ref transform, enableSecondary, enablePropagate);
            // var ret = _setBoneXSpaceHook.Original(thisRenderSkeleton, boneId, transform, enableSecondary, enablePropagate);
            // PluginLog.Log($"{(ulong) thisRenderSkeleton:X} {boneId} ");
            // PluginLog.Log($"{(ulong) thisRenderSkeleton:X} {boneId} {(ulong) transform:X} {enableSecondary} {enablePropagate} = {ret}");
         
            // Capture the transform memory space for us to use later
            _transformSpace = transform;
            
            // Return the bone id
            // Since this function operates on the RenderSkeleton, this is not a Havok function
            var ret = (ulong) boneId;
            return ret;
        }

        public void Dispose()
        {
            _setBoneModelSpaceHook.Disable();
            _setBoneModelSpaceHook.Dispose();
            _getBoneModelSpaceHook.Disable();
            _getBoneModelSpaceHook.Dispose();
            _syncAllHook.Disable();
            _syncAllHook.Dispose();
            _getSkeletonHook.Disable();
            _getSkeletonHook.Dispose();
            _executeSampleBlendJobHook.Disable();
            _executeSampleBlendJobHook.Dispose();
            ui.Dispose();
            pi.CommandManager.RemoveHandler(commandName);
            pi.Dispose();
        }

        private hkQsTransform* WriteToGameTransform(hkQsTransform transform)
        {
            var transformPtr = _transformSpace;
            transformPtr->Translation.x = transform.Translation.x;
            transformPtr->Translation.y = transform.Translation.y;
            transformPtr->Translation.z = transform.Translation.z;
            transformPtr->Rotation.x = transform.Rotation.x;
            transformPtr->Rotation.y = transform.Rotation.y;
            transformPtr->Rotation.z = transform.Rotation.z;
            transformPtr->Rotation.w = transform.Rotation.w;
            transformPtr->Scale.x = transform.Scale.x;
            transformPtr->Scale.y = transform.Scale.y;
            transformPtr->Scale.z = transform.Scale.z;
            return transformPtr;
        }

        private void UpdateBone(int actorId, ushort boneIndex, hkQsTransform boneTransform)
        {
            WriteToGameTransform(boneTransform);
            _setBoneModelSpaceHook.Original(GetRenderSkeletonByActorId(actorId)->PartialSkeletons, boneIndex, _transformSpace, false, true);
        }

        private void OnCommand(string command, string args)
        {
            _uiVisible = true;
        }

        private void DrawConfigUI()
        {
            _uiVisible = true;
        }

        private void UpdateChildren(hkaPose pose, short boneId)
        {
            var parents = pose.SkeletonPointer->ParentIndices.CopyToList();
            var children = new List<short>();
            for (short i = 0; i < parents.Count; i++)
            {
                if (parents[i] < 0 || parents[i] > parents.Count) continue;
                if (parents[i] == boneId)
                    children.Add(i);
            }

            foreach (var child in children)
            {
                _getBoneModelSpaceHook.Original(ref pose, child);
                UpdateChildren(pose, child);
            }
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
                    _setBoneModelSpaceHook.Enable();
                    _getBoneModelSpaceHook.Enable();
                    _syncAllHook.Enable();
                    _getSkeletonHook.Enable();
                    _executeSampleBlendJobHook.Enable();
                }
                else
                {
                    _setBoneModelSpaceHook.Disable();
                    _getBoneModelSpaceHook.Disable();
                    _syncAllHook.Disable();
                    _getSkeletonHook.Disable();
                    _executeSampleBlendJobHook.Disable();
                }
            }
            
            // if (ImGui.Checkbox("Enable UnknownHook", ref UnknownHookEnabled))
            // {
            //     if (UnknownHookEnabled)
            //         _getSkeletonHook.Enable();
            //     else
            //         _getSkeletonHook.Disable();
            // }

            ImGui.InputInt("idx", ref update);
            if (ImGui.Button("update"))
            {
                var renderSkele = GetRenderSkeletonByActorId(_selectedActorId);
                var pose = *renderSkele->PartialSkeletons[_selectedPartialSkeleIndex].Pose1;
                UpdateChildren(pose, (short) update);
            }
            
            ImGui.Text($"Last arg @ {_lastSetBoneModelSpaceSkeletonAddress:X}");

            if (ImGui.Selectable("Deselect"))
            {
                _selectedActorId = -1;
                _selectedPartialSkeleIndex = -1;
                _selectedBoneIndex = -1;
            }

            bool selectedActorFound = false;

            foreach (var actor in pi.ClientState.Actors)
            {
                if (actor.ActorId == _lastActorId)
                    selectedActorFound = true;
                
                var thisRenderSkele = RenderSkeleton.FromActor(actor);

                ClipboardTooltip($"{actor.Address.ToInt64():X}", "Copy Actor address");
                ImGui.SameLine();
                ClipboardTooltip($"{(ulong) thisRenderSkele:X}", "Copy Actor RenderSkeleton address");
                ImGui.SameLine();
                // ImGui.Text($"{actor.Address.ToInt64():X}:{(ulong) thisRenderSkele:X} - {actor.ObjectKind} - {actor.Name} - X{actor.Position.X} Y{actor.Position.Y} Z{actor.Position.Z} R{actor.Rotation}");
                ImGui.Text($" - {actor.ObjectKind} - {actor.Name} - X{actor.Position.X} Y{actor.Position.Y} Z{actor.Position.Z} R{actor.Rotation}");
                if (thisRenderSkele->PartialSkeletons != null)
                {
                    for (int i = 0; i < thisRenderSkele->PartialSkeletonNum; i++)
                    {
                        var pSkl = thisRenderSkele->PartialSkeletons[i];
                        if (pSkl.Pose1 != null)
                        {
                            if (ImGui.Selectable(pSkl.Pose1->ToString()))
                            {
                                _selectedActorId = actor.ActorId;
                                _selectedPartialSkeleIndex = i;
                                _selectedBoneIndex = -1;
                            }

                            if (ImGui.IsItemHovered())
                            {
                                DrawTooltip($"{thisRenderSkele->PartialSkeletons[i]}");
                            }
                        }
                    }
                }
            }

            if (_selectedActorId != _lastActorId)
            {
                _lastActorId = _selectedActorId;
            } else if (!selectedActorFound)
            {
                _selectedActorId = -1;
                _lastActorId = -1;
                _selectedPartialSkeleIndex = -1;
                _selectedBoneIndex = -1;
            }

            // if (renderSkele != _lastRenderSkele)
            // {
            //     _lastRenderSkele = renderSkele;
            //     _selectedPartialSkeleIndex = -1;
            //     _selectedBoneIndex = -1;
            // }

            ImGui.End();

            if (_selectedActorId != -1 && _selectedPartialSkeleIndex != -1)
            {
                var renderSkele = GetRenderSkeletonByActorId(_selectedActorId);
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
                    // var boneTransform = pose->ModelPose[_selectedBoneIndex];
                    var boneTransform = pose->LocalPose[_selectedBoneIndex];
                    // if (ImGui.SliderFloat("X", ref boneTransform.Rotation.x, -1.5f, 1.5f, "%.2f"))
                    //     pose->ModelPose[_selectedBoneIndex] = boneTransform;
                    // if (ImGui.SliderFloat("Y", ref boneTransform.Rotation.y, -1.5f, 1.5f, "%.2f"))
                    //     pose->ModelPose[_selectedBoneIndex] = boneTransform;
                    // if (ImGui.SliderFloat("Z", ref boneTransform.Rotation.z, -1.5f, 1.5f, "%.2f"))
                    //     pose->ModelPose[_selectedBoneIndex] = boneTransform;
                    // if (ImGui.SliderFloat("W", ref boneTransform.Rotation.w, -1.5f, 1.5f, "%.2f"))
                    //     pose->ModelPose[_selectedBoneIndex] = boneTransform;

                    // var initRot = PoseMath.FromQ(new Quaternion(boneTransform.Rotation.x, boneTransform.Rotation.y, boneTransform.Rotation.z, boneTransform.Rotation.w));
                    _currentRotations = PoseMath.FromQ(new Quaternion(boneTransform.Rotation.x, boneTransform.Rotation.y, boneTransform.Rotation.z, boneTransform.Rotation.w));

                    if (ImGui.SliderFloat("X", ref _currentRotations.X, -360f, 360f, "%.2f"))
                    {
                        // var newRot = PoseMath.ToQ(_currentRotations.X, _currentRotations.Y, _currentRotations.Z);
                        var newRot = PoseMath.ToQ(_currentRotations);
                        boneTransform.Rotation = new hkVector4()
                        {
                            x = newRot.X,
                            y = newRot.Y,
                            z = newRot.Z,
                            w = newRot.W
                        };
                        pose->LocalPose[_selectedBoneIndex] = boneTransform;
                        UpdateChildren(*pose, (short) _selectedBoneIndex);
                        // UpdateBone(_selectedActorId, (ushort) _selectedBoneIndex, boneTransform);
                    }

                    if (ImGui.SliderFloat("Y", ref _currentRotations.Y, -360f, 360f, "%.2f"))
                    {
                        // var newRot = PoseMath.ToQ(_currentRotations.X, _currentRotations.Y, _currentRotations.Z);
                        var newRot = PoseMath.ToQ(_currentRotations);
                        boneTransform.Rotation = new hkVector4()
                        {
                            x = newRot.X,
                            y = newRot.Y,
                            z = newRot.Z,
                            w = newRot.W
                        };
                        pose->LocalPose[_selectedBoneIndex] = boneTransform;
                        UpdateChildren(*pose, (short) _selectedBoneIndex);
                        // UpdateBone(_selectedActorId, (ushort) _selectedBoneIndex, boneTransform);
                    }

                    if (ImGui.SliderFloat("Z", ref _currentRotations.Z, -360f, 360f, "%.2f"))
                    {
                        // var newRot = PoseMath.ToQ(_currentRotations.X, _currentRotations.Y, _currentRotations.Z);
                        var newRot = PoseMath.ToQ(_currentRotations);
                        boneTransform.Rotation = new hkVector4()
                        {
                            x = newRot.X,
                            y = newRot.Y,
                            z = newRot.Z,
                            w = newRot.W
                        };
                        pose->LocalPose[_selectedBoneIndex] = boneTransform;
                        UpdateChildren(*pose, (short) _selectedBoneIndex);
                        // UpdateBone(_selectedActorId, (ushort) _selectedBoneIndex, boneTransform);
                    }
                    
                    ImGui.Separator();
                }

                ImGui.BeginChild("scrolling", new Vector2(0, -1));
                if (ImGui.Selectable("Deselect"))
                    _selectedBoneIndex = -1;
                for (int i = 0; i < selectedSkl->Bones.Length; i++)
                {
                    var boneName = selectedSkl->Bones[i].GetName();
                    if (ImGui.Selectable($"{i}:{boneName}"))
                    {
                        _selectedBoneIndex = i;
                        
                        // if (ImGui.SliderFloat("X", ref boneTransform.Rotation.x, -1.5f, 1.5f, "%.2f"))
                        //     pose->ModelPose[_selectedBoneIndex] = boneTransform;
                        // if (ImGui.SliderFloat("Y", ref boneTransform.Rotation.y, -1.5f, 1.5f, "%.2f"))
                        //     pose->ModelPose[_selectedBoneIndex] = boneTransform;
                        // if (ImGui.SliderFloat("Z", ref boneTransform.Rotation.z, -1.5f, 1.5f, "%.2f"))
                        //     pose->ModelPose[_selectedBoneIndex] = boneTransform;
                        // if (ImGui.SliderFloat("W", ref boneTransform.Rotation.w, -1.5f, 1.5f, "%.2f"))
                        //     pose->ModelPose[_selectedBoneIndex] = boneTransform;
                        var rotation = pose->ModelPose[_selectedBoneIndex].Rotation;
                        _currentRotations = PoseMath.FromQ(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w));
                        PluginLog.Log($"quat: {rotation.x:F2}, {rotation.y:F2}, {rotation.z:F2}, {rotation.w:F2}");
                        PluginLog.Log($"rot: {_currentRotations.X:F2}, {_currentRotations.Y:F2}, {_currentRotations.Z:F2}");
                        
                        // _currentRotations = PoseMath.FromQ2(new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w));
                        // _currentRotations = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w).ComputeAngles();
                        // if (_currentRotations.X < 0)
                        //     _currentRotations.X += 360;
                        // if (_currentRotations.Y < 0)
                        //     _currentRotations.Y += 360;
                        // if (_currentRotations.Z < 0)
                        //     _currentRotations.Z += 360;
                    }
                        
                }

                if (_selectedBoneIndex != -1)
                {
                    MouseoverBone(pose->ModelPose[_selectedBoneIndex].Translation, selectedSkl->Bones[_selectedBoneIndex].GetName());
                }

                ImGui.EndChild();

                ImGui.End();
            }
        }

        private void ClipboardTooltip(string content, string contextItemText)
        {
            ImGui.Text(content);
            if (ImGui.BeginPopupContextItem("clipboardcopy##content"))
            {
                if (ImGui.Selectable(contextItemText))
                    ImGui.SetClipboardText(content);
                ImGui.EndPopup();
            }
        }

        private RenderSkeleton* GetRenderSkeletonByActorId(int actorId)
        {
            var actor = GetActorByActorId(actorId);
            return actor == null ? null : RenderSkeleton.FromActor(actor);
        }

        private Actor GetActorByActorId(int actorId)
        {
            if (pi?.ClientState?.Actors == null) return null;

            var actor = pi.ClientState.Actors.Where(a => a.ActorId == actorId).ToArray();
            return actor.Any() ? actor.First() : null;
        }

        private void MouseoverBone(hkVector4 translate, string name)
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

        private unsafe void DrawSkeleTooltip(hkaSkeleton* skl)
        {
            ImGui.BeginTooltip();
            // for (int i = 0; i < skl->Bones.Length; i++)
            //     ImGui.Text($"{skl->Bones[i]}");
            ImGui.Text($"{*skl}");
            ImGui.EndTooltip();
        }
    }
}