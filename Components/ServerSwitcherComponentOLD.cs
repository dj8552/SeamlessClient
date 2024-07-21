﻿using HarmonyLib;
using Sandbox.Game.World;
using Sandbox;
using SeamlessClient.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Game;
using VRage.GameServices;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents;
using SpaceEngineers.Game.GUI;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Engine.Networking;
using System.Reflection;
using VRage.Network;
using Sandbox.ModAPI;
using VRageRender.Messages;
using VRageRender;
using Sandbox.Game.GUI;
using Sandbox.Game.World.Generator;
using Sandbox.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using SeamlessClient.ServerSwitching;
using System.Threading;
using System.Diagnostics;
using System.Runtime.Remoting.Metadata.W3cXsd2001;
using VRage.Scripting;
using EmptyKeys.UserInterface.Generated.StoreBlockView_Bindings;
using VRage.Game.Components;
using System.CodeDom;
using VRage.Collections;

namespace SeamlessClient.Components
{
    public class ServerSwitcherComponentOLD : ComponentBase
    {
        private static bool isSeamlessSwitching = false;
        private static ConstructorInfo TransportLayerConstructor;
        private static ConstructorInfo SyncLayerConstructor;
        private static ConstructorInfo ClientConstructor;

        private static MethodInfo UnloadProceduralWorldGenerator;
        private static MethodInfo GpsRegisterChat;
        private static MethodInfo LoadMembersFromWorld;
        private static MethodInfo InitVirtualClients;
        private static FieldInfo AdminSettings;
        private static FieldInfo RemoteAdminSettings;
        private static FieldInfo VirtualClients;
        private static FieldInfo SessionComponents;
        private static PropertyInfo MySessionLayer;

        public static MyGameServerItem TargetServer { get; private set; }
        public static MyObjectBuilder_World TargetWorld { get; private set; }

        public static ServerSwitcherComponentOLD Instance { get; private set; }
        private string OldArmorSkin { get; set; } = string.Empty;

        private static Stopwatch LoadTime = new Stopwatch();

        public ServerSwitcherComponentOLD() { Instance = this; }





        public override void Patch(Harmony patcher)
        {
            TransportLayerConstructor = PatchUtils.GetConstructor(PatchUtils.MyTransportLayerType, new[] { typeof(int) });
            SyncLayerConstructor = PatchUtils.GetConstructor(PatchUtils.SyncLayerType,  new[] { PatchUtils.MyTransportLayerType });
            ClientConstructor = PatchUtils.GetConstructor(PatchUtils.ClientType, new[] { typeof(MyGameServerItem), PatchUtils.SyncLayerType });
            MySessionLayer = PatchUtils.GetProperty(typeof(MySession), "SyncLayer");

            var onJoin = PatchUtils.GetMethod(PatchUtils.ClientType, "OnUserJoined");
            UnloadProceduralWorldGenerator = PatchUtils.GetMethod(typeof(MyProceduralWorldGenerator), "UnloadData");
            GpsRegisterChat = PatchUtils.GetMethod(typeof(MyGpsCollection), "RegisterChat");
            AdminSettings = PatchUtils.GetField(typeof(MySession), "m_adminSettings");
            RemoteAdminSettings = PatchUtils.GetField(typeof(MySession), "m_remoteAdminSettings");
            LoadMembersFromWorld = PatchUtils.GetMethod(typeof(MySession), "LoadMembersFromWorld");
            InitVirtualClients = PatchUtils.GetMethod(PatchUtils.VirtualClientsType, "Init");
            SessionComponents = PatchUtils.GetField(typeof(MySession), "m_loadOrder");
            VirtualClients = PatchUtils.GetField(typeof(MySession), "VirtualClients");
         

           patcher.Patch(onJoin, postfix: new HarmonyMethod(Get(typeof(ServerSwitcherComponentOLD), nameof(OnUserJoined))));
            base.Patch(patcher);
        }

        private static void OnUserJoined(ref JoinResultMsg msg)
        {
            if (msg.JoinResult == JoinResult.OK && isSeamlessSwitching)
            {
                //SeamlessClient.TryShow("User Joined! Result: " + msg.JoinResult.ToString());

                //Invoke the switch event
                ForceClientConnection();
                isSeamlessSwitching = false;
                LoadTime.Stop();

                MyAPIGateway.Utilities?.ShowMessage("Seamless", $"Loading Time: {LoadTime.Elapsed.ToString(@"s\.fff")}s");
            }
        }

        public void StartBackendSwitch(MyGameServerItem _TargetServer, MyObjectBuilder_World _TargetWorld)
        {
            isSeamlessSwitching = true;
            OldArmorSkin = MySession.Static.LocalHumanPlayer.BuildArmorSkin;
            TargetServer = _TargetServer;
            TargetWorld = _TargetWorld;


            LoadTime.Restart();
            MySandboxGame.Static.Invoke(delegate
            {
                //Set camera controller to fixed spectator
                MySession.Static.SetCameraController(MyCameraControllerEnum.SpectatorFixed);
                UnloadCurrentServer();
                SetNewMultiplayerClient();
                //SeamlessClient.IsSwitching = false;
               

            }, "SeamlessClient");

        }

        private void SetNewMultiplayerClient()
        {
            // Following is called when the multiplayer is set successfully to target server
            MySandboxGame.Static.SessionCompatHelper.FixSessionComponentObjectBuilders(TargetWorld.Checkpoint, TargetWorld.Sector);

            // Create constructors
            var LayerInstance = TransportLayerConstructor.Invoke(new object[] { 2 });
            var SyncInstance = SyncLayerConstructor.Invoke(new object[] { LayerInstance });
            var instance = ClientConstructor.Invoke(new object[] { TargetServer, SyncInstance });


            MyMultiplayer.Static = UtilExtensions.CastToReflected(instance, PatchUtils.ClientType);
            MyMultiplayer.Static.ExperimentalMode = true;



            // Set the new SyncLayer to the MySession.Static.SyncLayer
            MySessionLayer.SetValue(MySession.Static, MyMultiplayer.Static.SyncLayer);

            Seamless.TryShow("Successfully set MyMultiplayer.Static");


            Sync.Clients.SetLocalSteamId(Sync.MyId, false, MyGameService.UserName);
            Sync.Players.RegisterEvents();

        }



        private static void ForceClientConnection()
        {

            //Set World Settings
            SetWorldSettings();

            //Load force load any connected players
            LoadConnectedClients();



            MySector.InitEnvironmentSettings(TargetWorld.Sector.Environment);



            string text = ((!string.IsNullOrEmpty(TargetWorld.Checkpoint.CustomSkybox)) ? TargetWorld.Checkpoint.CustomSkybox : MySector.EnvironmentDefinition.EnvironmentTexture);
            MyRenderProxy.PreloadTextures(new string[1] { text }, TextureType.CubeMap);

            MyModAPIHelper.Initialize();
            MySession.Static.LoadDataComponents();


            //MySession.Static.LoadObjectBuildersComponents(TargetWorld.Checkpoint.SessionComponents);
            MyModAPIHelper.Initialize();
            // MySession.Static.LoadObjectBuildersComponents(TargetWorld.Checkpoint.SessionComponents);


            //MethodInfo A = typeof(MySession).GetMethod("LoadGameDefinition", BindingFlags.Instance | BindingFlags.NonPublic);
            // A.Invoke(MySession.Static, new object[] { TargetWorld.Checkpoint });



            MyMultiplayer.Static.OnSessionReady();

            UpdateSessionComponents(TargetWorld.Checkpoint.SessionComponents);
            StartEntitySync();


            MyHud.Chat.RegisterChat(MyMultiplayer.Static);
            GpsRegisterChat.Invoke(MySession.Static.Gpss, new object[] { MyMultiplayer.Static });


            // Allow the game to start proccessing incoming messages in the buffer
            MyMultiplayer.Static.StartProcessingClientMessages();

            //Recreate all controls... Will fix weird gui/paint/crap
            MyGuiScreenHudSpace.Static?.RecreateControls(true);
            //MySession.Static.LocalHumanPlayer.BuildArmorSkin = OldArmorSkin;

            //Pop any queued pause popup messages. Dirty way
            for(int i = 0; i < 10; i++)
                MySandboxGame.PausePop();
        }

        private static void LoadOnlinePlayers()
        {
            //Get This players ID

            MyPlayer.PlayerId? savingPlayerId = new MyPlayer.PlayerId(Sync.MyId);
            if (!savingPlayerId.HasValue)
            {
                Seamless.TryShow("SavingPlayerID is null! Creating Default!");
                savingPlayerId = new MyPlayer.PlayerId(Sync.MyId);
            }
            Seamless.TryShow("Saving PlayerID: " + savingPlayerId.ToString());

            Sync.Players.LoadConnectedPlayers(TargetWorld.Checkpoint, savingPlayerId);
            Sync.Players.LoadControlledEntities(TargetWorld.Checkpoint.ControlledEntities, TargetWorld.Checkpoint.ControlledObject, savingPlayerId);
            /*
          

            SeamlessClient.TryShow("Saving PlayerID: " + savingPlayerId.ToString());



            foreach (KeyValuePair<MyObjectBuilder_Checkpoint.PlayerId, MyObjectBuilder_Player> item3 in TargetWorld.Checkpoint.AllPlayersData.Dictionary)
            {
                MyPlayer.PlayerId playerId5 = new MyPlayer.PlayerId(item3.Key.GetClientId(), item3.Key.SerialId);

                SeamlessClient.TryShow($"ConnectedPlayer: {playerId5.ToString()}");
                if (savingPlayerId.HasValue && playerId5.SteamId == savingPlayerId.Value.SteamId)
                {
                    playerId5 = new MyPlayer.PlayerId(Sync.MyId, playerId5.SerialId);
                }

                Patches.LoadPlayerInternal.Invoke(MySession.Static.Players, new object[] { playerId5, item3.Value, false });
                ConcurrentDictionary<MyPlayer.PlayerId, MyPlayer> Players = (ConcurrentDictionary<MyPlayer.PlayerId, MyPlayer>)Patches.MPlayerGPSCollection.GetValue(MySession.Static.Players);
                //LoadPlayerInternal(ref playerId5, item3.Value);
                if (Players.TryGetValue(playerId5, out MyPlayer myPlayer))
                {
                    List<Vector3> value2 = null;
                    if (TargetWorld.Checkpoint.AllPlayersColors != null && TargetWorld.Checkpoint.AllPlayersColors.Dictionary.TryGetValue(item3.Key, out value2))
                    {
                        myPlayer.SetBuildColorSlots(value2);
                    }
                    else if (TargetWorld.Checkpoint.CharacterToolbar != null && TargetWorld.Checkpoint.CharacterToolbar.ColorMaskHSVList != null && TargetWorld.Checkpoint.CharacterToolbar.ColorMaskHSVList.Count > 0)
                    {
                        myPlayer.SetBuildColorSlots(TargetWorld.Checkpoint.CharacterToolbar.ColorMaskHSVList);
                    }
                }
            }

            */

        }

        private static void SetWorldSettings()
        {
            //MyEntities.MemoryLimitAddFailureReset();

            //Clear old list
            MySession.Static.PromotedUsers.Clear();
            MySession.Static.CreativeTools.Clear();
            Dictionary<ulong, AdminSettingsEnum> AdminSettingsList = (Dictionary<ulong, AdminSettingsEnum>)RemoteAdminSettings.GetValue(MySession.Static);
            AdminSettingsList.Clear();



            // Set new world settings
            MySession.Static.Name = MyStatControlText.SubstituteTexts(TargetWorld.Checkpoint.SessionName);
            MySession.Static.Description = TargetWorld.Checkpoint.Description;

            MySession.Static.Mods = TargetWorld.Checkpoint.Mods;
            MySession.Static.Settings = TargetWorld.Checkpoint.Settings;
            MySession.Static.CurrentPath = MyLocalCache.GetSessionSavesPath(MyUtils.StripInvalidChars(TargetWorld.Checkpoint.SessionName), contentFolder: false, createIfNotExists: false);
            MySession.Static.WorldBoundaries = TargetWorld.Checkpoint.WorldBoundaries;
            MySession.Static.InGameTime = MyObjectBuilder_Checkpoint.DEFAULT_DATE;
            MySession.Static.ElapsedGameTime = new TimeSpan(TargetWorld.Checkpoint.ElapsedGameTime);
            MySession.Static.Settings.EnableSpectator = false;

            MySession.Static.Password = TargetWorld.Checkpoint.Password;
            MySession.Static.PreviousEnvironmentHostility = TargetWorld.Checkpoint.PreviousEnvironmentHostility;
            MySession.Static.RequiresDX = TargetWorld.Checkpoint.RequiresDX;
            MySession.Static.CustomLoadingScreenImage = TargetWorld.Checkpoint.CustomLoadingScreenImage;
            MySession.Static.CustomLoadingScreenText = TargetWorld.Checkpoint.CustomLoadingScreenText;
            MySession.Static.CustomSkybox = TargetWorld.Checkpoint.CustomSkybox;
            MyAPIUtilities.Static.Variables = TargetWorld.Checkpoint.ScriptManagerData.variables.Dictionary;

           


            try
            {
                MySession.Static.Gpss = new MyGpsCollection();
                MySession.Static.Gpss.LoadGpss(TargetWorld.Checkpoint);

            }
            catch (Exception ex)
            {
                Seamless.TryShow($"An error occurred while loading GPS points! You will have an empty gps list! \n {ex.ToString()}");
            }


            MyRenderProxy.RebuildCullingStructure();
            //MySession.Static.Toolbars.LoadToolbars(checkpoint);

            Sync.Players.RespawnComponent.InitFromCheckpoint(TargetWorld.Checkpoint);


            // Set new admin settings
            if (TargetWorld.Checkpoint.PromotedUsers != null)
            {
                MySession.Static.PromotedUsers = TargetWorld.Checkpoint.PromotedUsers.Dictionary;
            }
            else
            {
                MySession.Static.PromotedUsers = new Dictionary<ulong, MyPromoteLevel>();
            }




            foreach (KeyValuePair<MyObjectBuilder_Checkpoint.PlayerId, MyObjectBuilder_Player> item in TargetWorld.Checkpoint.AllPlayersData.Dictionary)
            {
                ulong clientId = item.Key.GetClientId();
                AdminSettingsEnum adminSettingsEnum = (AdminSettingsEnum)item.Value.RemoteAdminSettings;
                if (TargetWorld.Checkpoint.RemoteAdminSettings != null && TargetWorld.Checkpoint.RemoteAdminSettings.Dictionary.TryGetValue(clientId, out var value))
                {
                    adminSettingsEnum = (AdminSettingsEnum)value;
                }
                if (!MyPlatformGameSettings.IsIgnorePcuAllowed)
                {
                    adminSettingsEnum &= ~AdminSettingsEnum.IgnorePcu;
                    adminSettingsEnum &= ~AdminSettingsEnum.KeepOriginalOwnershipOnPaste;
                }


                AdminSettingsList[clientId] = adminSettingsEnum;
                if (!Sync.IsDedicated && clientId == Sync.MyId)
                {
                    AdminSettings.SetValue(MySession.Static, adminSettingsEnum);

                    //m_adminSettings = adminSettingsEnum;
                }



                if (!MySession.Static.PromotedUsers.TryGetValue(clientId, out var value2))
                {
                    value2 = MyPromoteLevel.None;
                }
                if (item.Value.PromoteLevel > value2)
                {
                    MySession.Static.PromotedUsers[clientId] = item.Value.PromoteLevel;
                }
                if (!MySession.Static.CreativeTools.Contains(clientId) && item.Value.CreativeToolsEnabled)
                {
                    MySession.Static.CreativeTools.Add(clientId);
                }
            }




        }

        private static void LoadConnectedClients()
        {

            LoadMembersFromWorld.Invoke(MySession.Static, new object[] { TargetWorld, MyMultiplayer.Static });


            //Re-Initilize Virtual clients
            object VirtualClientsValue = VirtualClients.GetValue(MySession.Static);
            InitVirtualClients.Invoke(VirtualClientsValue, null);


            //load online players
            LoadOnlinePlayers();

        }

        private static void StartEntitySync()
        {
            Seamless.TryShow("Requesting Player From Server");
            Sync.Players.RequestNewPlayer(Sync.MyId, 0, MyGameService.UserName, null, realPlayer: true, initialPlayer: true);
            if (MySession.Static.ControlledEntity == null && Sync.IsServer && !Sandbox.Engine.Platform.Game.IsDedicated)
            {
                MyLog.Default.WriteLine("ControlledObject was null, respawning character");
                //m_cameraAwaitingEntity = true;
                MyPlayerCollection.RequestLocalRespawn();
            }

            //Request client state batch
            (MyMultiplayer.Static as MyMultiplayerClientBase).RequestBatchConfirmation();
            MyMultiplayer.Static.PendingReplicablesDone += MyMultiplayer_PendingReplicableDone;
            //typeof(MyGuiScreenTerminal).GetMethod("CreateTabs")

            MySession.Static.LoadDataComponents();
            //Session.Static.LoadObjectBuildersComponents(TargetWorld.Checkpoint.SessionComponents);
            //MyGuiSandbox.LoadData(false);
            //MyGuiSandbox.AddScreen(MyGuiSandbox.CreateScreen(MyPerGameSettings.GUI.HUDScreen));
            MyRenderProxy.RebuildCullingStructure();
            MyRenderProxy.CollectGarbage();

            Seamless.TryShow("OnlinePlayers: " + MySession.Static.Players.GetOnlinePlayers().Count);
            Seamless.TryShow("Loading Complete!");
        }

        private static void MyMultiplayer_PendingReplicableDone()
        {
            if (MySession.Static.VoxelMaps.Instances.Count > 0)
            {
                MySandboxGame.AreClipmapsReady = false;
            }
            MyMultiplayer.Static.PendingReplicablesDone -= MyMultiplayer_PendingReplicableDone;
        }

        private void UnloadCurrentServer()
        {
            //Unload current session on game thread
            if (MyMultiplayer.Static == null)
                throw new Exception("MyMultiplayer.Static is null on unloading? dafuq?");


            RemoveOldEntities();

            //Try and close the quest log
            MySessionComponentIngameHelp component = MySession.Static.GetComponent<MySessionComponentIngameHelp>();
            component?.TryCancelObjective();


            Sync.Clients.Clear();
            Sync.Players.ClearPlayers();


            MyHud.Chat.UnregisterChat(MyMultiplayer.Static);




            MySession.Static.Gpss.RemovePlayerGpss(MySession.Static.LocalPlayerId);
            MyHud.GpsMarkers.Clear();
            MyMultiplayer.Static.ReplicationLayer.Disconnect();
            UnloadSessionComponents();


            MyMultiplayer.Static.ReplicationLayer.Dispose();

            

            MyMultiplayer.Static.Dispose();

            //Clear all old players and clients.

            MyMultiplayer.Static = null;

            //Close any respawn screens that are open
            MyGuiScreenMedicals.Close();

            //MySession.Static.UnloadDataComponents();

        }

        private void RemoveOldEntities()
        {
            foreach (var ent in MyEntities.GetEntities())
            {
                if (ent is MyPlanet)
                    continue;

                ent.Close();
            }
        }

        private void UnloadSessionComponents()
        {
            List<MySessionComponentBase> sessions = (List<MySessionComponentBase>)SessionComponents.GetValue(MySession.Static);

            foreach(var session in sessions)
            {
                if(session.ModContext == null)
                    continue;

                if (session.Initialized == false)
                    continue;


                MethodInfo unload = PatchUtils.GetMethod(typeof(MySessionComponentBase), "UnloadData");
                unload.Invoke(session, null);
                FieldInfo inited = PatchUtils.GetField(typeof(MySessionComponentBase), "m_initialized");
                inited.SetValue(session, false);


                MyLog.Default.WriteLine($"{session.GetType()}");
            }
        }

        static List<Type> ValidInitTypes = new List<Type>() { typeof(MyProceduralWorldGenerator), typeof(MySessionComponentSafeZones) };

        private static void UpdateSessionComponents(List<MyObjectBuilder_SessionComponent> objectBuilderData)
        {

            FieldInfo sessionComps = PatchUtils.GetField(typeof(MySession), "m_sessionComponents");
            CachingDictionary<Type, MySessionComponentBase>  dict = (CachingDictionary<Type, MySessionComponentBase>)sessionComps.GetValue(MySession.Static);

            foreach (var entity in objectBuilderData)
            {

                Type t =  MySessionComponentMapping.TryGetMappedSessionComponentType(entity.GetType());
                if(dict.TryGetValue(t, out var component) & ValidInitTypes.Contains(component.GetType()))
                {
                    component.Init(entity);
                    component.LoadData();
                }
            }


        }






    }
}
