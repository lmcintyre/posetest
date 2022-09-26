using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ImGuiNET;

namespace PoseTest
{
    public unsafe class PoseTestPlugin : IDalamudPlugin
    {
        public string Name => "Pose Test";

        private const string commandName = "/posetest";

        // private delegate IntPtr SetBoneXSpacePrototype(void* thisRenderSkeleton, ushort boneId, ref hkQsTransform, bool enableSecondary, bool enablePropagate);
        private delegate ulong SetBoneModelSpaceFfxivPrototype(PartialSkeleton* partialSkeleton, ushort boneId, hkQsTransform* transform, bool enableSecondary, bool enablePropagate);
        private Hook<SetBoneModelSpaceFfxivPrototype> _setBoneModelSpaceFfxivHook;

        private delegate hkQsTransform* CalculateBoneModelSpacePrototype(ref hkaPose pose, int boneIdx);
        private Hook<CalculateBoneModelSpacePrototype> _calculateBoneModelSpaceHook;

        // It's also possible that this is hkaPose::SyncModel
        private delegate void SyncModelSpacePrototype(ref hkaPose pose);
        private Hook<SyncModelSpacePrototype> _syncModelSpaceHook;

        private delegate void SyncLocalSpacePrototype(ref hkaPose pose);
        private Hook<SyncLocalSpacePrototype> _syncLocalSpaceHook;
        
        private delegate hkaSkeleton* SetToReferencePosePrototype(ref hkaPose pose);
        private Hook<SetToReferencePosePrototype> _setToReferencePoseHook;

        private delegate void ExecuteSampleBlendJobPrototype(void* job);
        private Hook<ExecuteSampleBlendJobPrototype> _executeSampleBlendJobHook;
        
        private delegate void SkeletonUpdaterJobFunc(void* updater, void* job);
        private Hook<SkeletonUpdaterJobFunc> _skeletonUpdaterJobFuncHook;
        
        private delegate IntPtr HkaBlendJobBuild(void* job, void* skel, void* bonesOut, void* floatsOut, byte convertToModel, int numBones, int numFloats);
        private Hook<HkaBlendJobBuild> _hkaBlendJobBuildHook;

        // UI
        private bool _uiVisible = true;
        private bool _updatePaused = false;
        private uint _lastActorId = uint.MaxValue;
        private uint _selectedActorId = uint.MaxValue;
        private uint _selectedPartialSkeleIndex = uint.MaxValue;
        private uint _selectedBoneIndex = uint.MaxValue;
        
        private bool _calculateBoneModelSpacePaused = false;
        private bool _setBoneModelSpaceFfxivPaused = false;
        private bool _syncModelSpacePaused = false;
        private bool _syncLocalSpacePaused = false;
        private bool _setToReferencePosePaused = false;
        private bool _executeSampleBlendJobPaused = false;
        private bool _skeletonUpdaterJobFuncPaused = false;
        private bool _hkaBlendJobBuildPaused = false;

        private bool UnknownHookEnabled = false;
        private int update = 0;

        private Vector3 _currentRotations = Vector3.Zero;
        private hkQsTransform* _transformSpace = null;

        private ulong _lastSetBoneModelSpaceSkeletonAddress = 0;
        
        private DalamudPluginInterface _pi;
        private CommandManager _commandManager;
        private ObjectTable _objectTable;
        private ClientState _clientState;
        private GameGui _gameGui;
        
        public Configuration configuration;
        private PluginUI ui;
        
        public PoseTestPlugin(
            DalamudPluginInterface pluginInterface,
            CommandManager commandManager,
            ObjectTable objectTable,
            ClientState clientState,
            GameGui gameGui,
            SigScanner scanner
            )
        {
            _pi = pluginInterface;
            _commandManager = commandManager;
            _objectTable = objectTable;
            _clientState = clientState;
            _gameGui = gameGui;

            configuration = _pi.GetPluginConfig() as Configuration ?? new Configuration();
            configuration.Initialize(_pi);
            ui = new PluginUI(configuration, this);
            
            _commandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "A useful message to display in /xlhelp"
            });

            try
            {
                var setBoneModelSpacePtr = scanner.ScanText("48 8B C4 48 89 58 18 55 56 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 0F 29 70 B8 0F 29 78 A8 44 0F 29 40 ?? 44 0F 29 48 ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B B1");
                _setBoneModelSpaceFfxivHook = Hook<SetBoneModelSpaceFfxivPrototype>.FromAddress(setBoneModelSpacePtr, (SetBoneModelSpaceFfxivPrototype) SetBoneModelSpaceFfxivDetour);

                var getBoneModelSpacePtr = scanner.ScanText("40 53 48 83 EC 10 4C 8B 49 28");
                _calculateBoneModelSpaceHook = Hook<CalculateBoneModelSpacePrototype>.FromAddress(getBoneModelSpacePtr, (CalculateBoneModelSpacePrototype) CalculateBoneModelSpaceDetour);

                var syncAllPtr = scanner.ScanText("48 83 EC 18 80 79 38 00 0F 85 ?? ?? ?? ?? 48 8B 01 45 33 C9 48 89 5C 24 ?? 48 63 58 30 48 85 DB 0F 8E ?? ?? ?? ?? 45 8B D1");
                _syncModelSpaceHook = Hook<SyncModelSpacePrototype>.FromAddress(syncAllPtr, (SyncModelSpacePrototype) SyncModelSpaceDetour);
                
                var syncLocalSpacePtr = scanner.ScanText("4C 8B DC 53 48 81 EC ?? ?? ?? ?? 80 79 39 00");
                _syncLocalSpaceHook = Hook<SyncLocalSpacePrototype>.FromAddress(syncLocalSpacePtr, (SyncLocalSpacePrototype) SyncLocalSpaceDetour);

                var getSkeletonPtr = scanner.ScanText("48 8B 01 4C 8B C9 4C 8B 41 08 48 8B 50 38 8B 40 30 8D 0C 40 85 C9 74 1C 0F 1F 84 00");
                _setToReferencePoseHook = Hook<SetToReferencePosePrototype>.FromAddress(getSkeletonPtr, (SetToReferencePosePrototype) SetToReferencePoseDetour);
                
                // ExecuteSampleBlendJob?
                var executeSampleBlendJobPtr = scanner.ScanText("48 8B C4 55 57 48 8D 68 98 48 81 EC ?? ?? ?? ?? 66 83 79 ?? ?? 48 8B F9 0F 84 ?? ?? ?? ?? 8B 0D");
                _executeSampleBlendJobHook = Hook<ExecuteSampleBlendJobPrototype>.FromAddress(executeSampleBlendJobPtr, (ExecuteSampleBlendJobPrototype) ExecuteSampleBlendJobDetour);

                var skeletonUpdaterJobFuncPtr = scanner.ScanText("48 89 4C 24 ?? 53 56 57 41 57");
                _skeletonUpdaterJobFuncHook = Hook<SkeletonUpdaterJobFunc>.FromAddress(skeletonUpdaterJobFuncPtr, (SkeletonUpdaterJobFunc) SkeletonUpdaterJobFuncDetour);

                var hkaBlendJobBuildPtr = scanner.ScanText("E8 ?? ?? ?? ?? 4C 8B BC 24 ?? ?? ?? ?? EB 02");
                _hkaBlendJobBuildHook = Hook<HkaBlendJobBuild>.FromAddress(hkaBlendJobBuildPtr, (HkaBlendJobBuild) HkaBlendJobBuildDetour);
            }
            catch (KeyNotFoundException) { }

            PluginLog.Log($"PoseTest enabled!");

            _pi.UiBuilder.DisableAutomaticUiHide = true;
            _pi.UiBuilder.Draw += DrawUI;
            _pi.UiBuilder.OpenConfigUi += DrawConfigUI;
        }
        

        private IntPtr HkaBlendJobBuildDetour(void* job, void* skel, void* bonesout, void* floatsout, byte converttomodel, int numbones, int numfloats)
        {
            return _hkaBlendJobBuildHook.Original(job, skel, bonesout, floatsout, converttomodel, 0, 0);
        }

        private void SkeletonUpdaterJobFuncDetour(void* updater, void* job)
        {
            // noppypoo
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
        
        private hkaSkeleton* SetToReferencePoseDetour(ref hkaPose pose)
        {
            // The original function will for some reason attempt to sync/modify local space
            // so we'll just return the skeleton address as expected
            var hkaSkl = pose.SkeletonPointer;
            return hkaSkl;
        }

        private void SyncModelSpaceDetour(ref hkaPose pose)
        {
            // Do nothing so the pose will not sync
            
            // PluginLog.Log($"UnknownDetour: {(ulong) alsoUnknown:X}");
            // UnknownHook.Original(alsoUnknown);
        }
        
        private void SyncLocalSpaceDetour(ref hkaPose pose)
        {
            
        }

        private hkQsTransform* CalculateBoneModelSpaceDetour(ref hkaPose pose, int boneIdx)
        {
            // The original function will attempt to sync poses between spaces
            // so we'll just return the bone's model space transform address as expected
            var boneTransformPtr = pose.ModelPose.Data;
            return boneTransformPtr + boneIdx;
        }

        // private IntPtr SetBoneXSpaceDetour(void* thisRenderSkeleton, ushort boneId, ref hkQsTransform transform, bool enableSecondary, bool enablePropagate)
        private ulong SetBoneModelSpaceFfxivDetour(PartialSkeleton* partialSkeletons, ushort boneId, hkQsTransform* transform, bool enableSecondary, bool enablePropagate)
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
            _setBoneModelSpaceFfxivHook.Disable();
            _setBoneModelSpaceFfxivHook.Dispose();
            _calculateBoneModelSpaceHook.Disable();
            _calculateBoneModelSpaceHook.Dispose();
            _syncModelSpaceHook.Disable();
            _syncModelSpaceHook.Dispose();
            _syncLocalSpaceHook.Disable();
            _syncLocalSpaceHook.Dispose();
            _setToReferencePoseHook.Disable();
            _setToReferencePoseHook.Dispose();
            _executeSampleBlendJobHook.Disable();
            _executeSampleBlendJobHook.Dispose();
            _skeletonUpdaterJobFuncHook?.Disable();
            _skeletonUpdaterJobFuncHook?.Dispose();
            _hkaBlendJobBuildHook?.Disable();
            _hkaBlendJobBuildHook?.Dispose();
            ui.Dispose();
            _commandManager.RemoveHandler(commandName);
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

        private void UpdateBone(uint actorId, ushort boneIndex, hkQsTransform boneTransform)
        {
            WriteToGameTransform(boneTransform);
            _setBoneModelSpaceFfxivHook.Original(GetRenderSkeletonByActorId(actorId)->PartialSkeletons, boneIndex, _transformSpace, false, true);
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
                _calculateBoneModelSpaceHook.Original(ref pose, child);
                UpdateChildren(pose, child);
            }
        }

        private unsafe void DrawUI()
        {
            if (!_uiVisible)
                return;

            ImGui.Begin("PoseTest", ref _uiVisible);

            // if (ImGui.Checkbox("Pause all skeleton updates", ref _updatePaused))
            // {
            //     if (_updatePaused)
            //     {
            //         // _setBoneModelSpaceHook.Enable();
            //         // _getBoneModelSpaceHook.Enable();
            //         _syncAllHook.Enable();
            //         // _getSkeletonHook.Enable();
            //         _executeSampleBlendJobHook.Enable();
            //     }
            //     else
            //     {
            //         _setBoneModelSpaceHook.Disable();
            //         _getBoneModelSpaceHook.Disable();
            //         _syncAllHook.Disable();
            //         _getSkeletonHook.Disable();
            //         _executeSampleBlendJobHook.Disable();
            //     }
            // }
            
            var calculateBoneModelSpaceText = _calculateBoneModelSpacePaused ? "Unpause CalculateBoneModelSpace" : "Pause CalculateBoneModelSpace";
            var setBoneModelSpaceFfxivText = _setBoneModelSpaceFfxivPaused ? "Unpause SetBoneModelSpaceFfxiv" : "Pause SetBoneModelSpaceFfxiv";
            var syncModelSpaceText = _syncModelSpacePaused ? "Unpause SyncModelSpace" : "Pause SyncModelSpace";
            var syncLocalSpaceText = _syncLocalSpacePaused ? "Unpause SyncLocalSpace" : "Pause SyncLocalSpace";
            var setToReferencePoseText = _setToReferencePosePaused ? "Unpause SetToReferencePose" : "Pause SetToReferencePose";
            var executeSampleBlendJobText = _executeSampleBlendJobPaused ? "Unpause ExecuteSampleBlendJob" : "Pause ExecuteSampleBlendJob";
            var skeletonUpdaterJobFuncText = _skeletonUpdaterJobFuncPaused ? "Unpause SkeletonUpdaterJobFunc" : "Pause SkeletonUpdaterJobFunc";
            var hkaBlendJobBuildText = _hkaBlendJobBuildPaused ? "Unpause HkaBlendJobBuild" : "Pause HkaBlendJobBuild";

            if (ImGui.Button(calculateBoneModelSpaceText))
            {
                if (_calculateBoneModelSpacePaused)
                {
                    _calculateBoneModelSpaceHook.Disable();
                    _calculateBoneModelSpacePaused = false;
                }
                else
                {
                    _calculateBoneModelSpaceHook.Enable();
                    _calculateBoneModelSpacePaused = true;
                }
            }
            
            if (ImGui.Button(setBoneModelSpaceFfxivText))
            {
                if (_setBoneModelSpaceFfxivPaused)
                {
                    _setBoneModelSpaceFfxivHook.Disable();
                    _setBoneModelSpaceFfxivPaused = false;
                }
                else
                {
                    _setBoneModelSpaceFfxivHook.Enable();
                    _setBoneModelSpaceFfxivPaused = true;
                }
            }
            
            if (ImGui.Button(syncModelSpaceText))
            {
                if (_syncModelSpacePaused)
                {
                    _syncModelSpaceHook.Disable();
                    _syncModelSpacePaused = false;
                }
                else
                {
                    _syncModelSpaceHook.Enable();
                    _syncModelSpacePaused = true;
                }
            }
            
            if (ImGui.Button(syncLocalSpaceText))
            {
                if (_syncLocalSpacePaused)
                {
                    _syncLocalSpaceHook.Disable();
                    _syncLocalSpacePaused = false;
                }
                else
                {
                    _syncLocalSpaceHook.Enable();
                    _syncLocalSpacePaused = true;
                }
            }
            
            if (ImGui.Button(setToReferencePoseText))
            {
                if (_setToReferencePosePaused)
                {
                    _setToReferencePoseHook.Disable();
                    _setToReferencePosePaused = false;
                }
                else
                {
                    _setToReferencePoseHook.Enable();
                    _setToReferencePosePaused = true;
                }
            }
            
            if (ImGui.Button(executeSampleBlendJobText))
            {
                if (_executeSampleBlendJobPaused)
                {
                    _executeSampleBlendJobHook.Disable();
                    _executeSampleBlendJobPaused = false;
                }
                else
                {
                    _executeSampleBlendJobHook.Enable();
                    _executeSampleBlendJobPaused = true;
                }
            }
            
            if (ImGui.Button(skeletonUpdaterJobFuncText))
            {
                if (_skeletonUpdaterJobFuncPaused)
                {
                    _skeletonUpdaterJobFuncHook.Disable();
                    _skeletonUpdaterJobFuncPaused = false;
                }
                else
                {
                    _skeletonUpdaterJobFuncHook.Enable();
                    _skeletonUpdaterJobFuncPaused = true;
                }
            }
            
            if (ImGui.Button(hkaBlendJobBuildText))
            {
                if (_hkaBlendJobBuildPaused)
                {
                    _hkaBlendJobBuildHook.Disable();
                    _hkaBlendJobBuildPaused = false;
                }
                else
                {
                    _hkaBlendJobBuildHook.Enable();
                    _hkaBlendJobBuildPaused = true;
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
                _selectedActorId = uint.MaxValue;
                _selectedPartialSkeleIndex = uint.MaxValue;
                _selectedBoneIndex = uint.MaxValue;
            }

            bool selectedActorFound = false;

            foreach (var actor in _objectTable)
            {
                if (actor.ObjectId == _lastActorId)
                    selectedActorFound = true;
                
                var thisRenderSkele = RenderSkeleton.FromActor((GameObject*) actor.Address);

                ClipboardTooltip($"{actor.Address.ToInt64():X}", "Copy Actor address");
                ImGui.SameLine();
                ClipboardTooltip($"{(ulong) thisRenderSkele:X}", "Copy Actor RenderSkeleton address");
                ImGui.SameLine();
                // ImGui.Text($"{actor.Address.ToInt64():X}:{(ulong) thisRenderSkele:X} - {actor.ObjectKind} - {actor.Name} - X{actor.Position.X} Y{actor.Position.Y} Z{actor.Position.Z} R{actor.Rotation}");
                ImGui.Text($" - {actor.ObjectKind} - {actor.Name} - X{actor.Position.X} Y{actor.Position.Y} Z{actor.Position.Z} R{actor.Rotation}");
                if (thisRenderSkele != null && thisRenderSkele->PartialSkeletons != null)
                {
                    for (uint i = 0; i < thisRenderSkele->PartialSkeletonNum; i++)
                    {
                        var pSkl = thisRenderSkele->PartialSkeletons[i];
                        if (pSkl.Pose1 != null)
                        {
                            if (ImGui.Selectable(pSkl.Pose1->ToString()))
                            {
                                _selectedActorId = actor.ObjectId;
                                _selectedPartialSkeleIndex = i;
                                _selectedBoneIndex = uint.MaxValue;
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
                _selectedActorId = uint.MaxValue;
                _lastActorId = uint.MaxValue;
                _selectedPartialSkeleIndex = uint.MaxValue;
                _selectedBoneIndex = uint.MaxValue;
            }

            // if (renderSkele != _lastRenderSkele)
            // {
            //     _lastRenderSkele = renderSkele;
            //     _selectedPartialSkeleIndex = -1;
            //     _selectedBoneIndex = -1;
            // }

            ImGui.End();

            if (_selectedActorId != uint.MaxValue && _selectedPartialSkeleIndex != uint.MaxValue)
            {
                var renderSkele = GetRenderSkeletonByActorId(_selectedActorId);
                var pose = renderSkele->PartialSkeletons[_selectedPartialSkeleIndex].Pose1;
                var selectedSkl = pose->SkeletonPointer;
                
                ImGui.SetNextWindowSize(new Vector2(200, 600), ImGuiCond.FirstUseEver);
                ImGui.Begin(selectedSkl->GetName());
                
                ClipboardText($"RenderSkele: {(ulong)renderSkele:X}", $"{(ulong)renderSkele:X}");
                ClipboardText($"Pose: {(ulong)pose:X}", $"{(ulong)pose:X}");
                ClipboardText($"Skeleton: {(ulong)selectedSkl:X}", $"{(ulong)selectedSkl:X}");
                
                if (_selectedBoneIndex != uint.MaxValue)
                {
                    ImGui.Text($"Bone: {selectedSkl->Bones[_selectedBoneIndex]}");
                    var parentIndex = selectedSkl->ParentIndices[_selectedBoneIndex];
                    var boneParentName = parentIndex == -1 ? "" : selectedSkl->Bones[parentIndex].GetName();
                    ImGui.Text($"Bone parent: {boneParentName}");
                    ImGui.Separator();
                    // var boneTransform = pose->ModelPose[_selectedBoneIndex];
                    var boneTransform = pose->LocalPose[_selectedBoneIndex];
                    // var t = (ulong) (pose->LocalPose).Data + (48 * _selectedBoneIndex);
                    var t = (ulong) (pose->ModelPose).Data + (48 * _selectedBoneIndex);
                    ClipboardText($"Bone transform: {t:X}", $"{t:X}");
                    
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
                        // pose->ModelPose[_selectedBoneIndex] = boneTransform;
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
                        // pose->ModelPose[_selectedBoneIndex] = boneTransform;
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
                        // pose->ModelPose[_selectedBoneIndex] = boneTransform;
                        UpdateChildren(*pose, (short) _selectedBoneIndex);
                        // UpdateBone(_selectedActorId, (ushort) _selectedBoneIndex, boneTransform);
                    }
                    
                    ImGui.Separator();
                }

                ImGui.BeginChild("scrolling", new Vector2(0, -1));
                if (ImGui.Selectable("Deselect"))
                    _selectedBoneIndex = uint.MaxValue;
                for (uint i = 0; i < selectedSkl->Bones.Length; i++)
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

                if (_selectedBoneIndex != uint.MaxValue)
                {
                    DrawBone(pose->ModelPose[_selectedBoneIndex].Translation, selectedSkl->Bones[_selectedBoneIndex].GetName());
                }

                ImGui.EndChild();

                ImGui.End();
            }
        }

        private void ClipboardTooltip(string content, string contextItemText)
        {
            ImGui.Text(content);
            if (ImGui.BeginPopupContextItem($"{content}"))
            {
                if (ImGui.Selectable(contextItemText))
                    ImGui.SetClipboardText(content);
                ImGui.EndPopup();
            }
        }

        private void ClipboardText(string text, string copy)
        {
            ImGui.Text(text);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                ImGui.SetClipboardText(copy);
            }
        }

        private RenderSkeleton* GetRenderSkeletonByActorId(uint actorId)
        {
            var actor = GetActorByActorId(actorId);
            return actor == null ? null : RenderSkeleton.FromActor(actor);
        }

        private GameObject* GetActorByActorId(uint actorId)
        {
            var actor = _objectTable.Where(a => a.ObjectId == actorId).ToArray();
            return actor.Any() ? (GameObject*) actor.First().Address : (GameObject*) IntPtr.Zero;
        }

        private void DrawBone(hkVector4 translate, string name)
        {
            var lp = _clientState.LocalPlayer;
            if (lp == null) return;

            var coords = new Vector3
            {
                X = lp.Position.X + translate.x,
                Y = lp.Position.Z + translate.y,
                Z = lp.Position.Y + translate.z
            };

            if (!_gameGui.WorldToScreen(coords, out var pos)) return;

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