/*
 * Copyright (c) Contributors, http://whitecore-sim.org/, http://aurora-sim.org
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the WhiteCore-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using WhiteCore.Framework.ClientInterfaces;
using WhiteCore.Framework.ConsoleFramework;
using WhiteCore.Framework.DatabaseInterfaces;
using WhiteCore.Framework.Modules;
using WhiteCore.Framework.PresenceInfo;
using WhiteCore.Framework.SceneInfo;
using WhiteCore.Framework.Services;
using WhiteCore.Framework.Services.ClassHelpers.Profile;
using WhiteCore.Framework.Utilities;
using RegionFlags = WhiteCore.Framework.Services.RegionFlags;

namespace WhiteCore.Modules.Estate
{
    public class EstateSettingsModule : ISharedRegionStartupModule
    {
        #region Declares

        readonly Dictionary<UUID, int> lastTelehub = new Dictionary<UUID, int> ();
        readonly Dictionary<UUID, int> timeSinceLastTeleport = new Dictionary<UUID, int> ();
        IScene m_scene;
        string [] banCriteria = new string [0];
        bool forceLandingPointsOnCrossing;
        bool loginsDisabled = true;
        IRegionConnector regionConnector;
        float secondsBeforeNextTeleport = 3;
        bool startDisabled;
        bool m_enabled;
        bool m_enabledBlockTeleportSeconds;
        bool m_checkMaturityLevel = true;

        #endregion

        #region ISharedRegionStartupModule Members

        public void Initialise (IScene scene, IConfigSource source, ISimulationBase simBase)
        {
            IConfig config = source.Configs ["EstateSettingsModule"];
            if (config != null) {
                m_enabled = config.GetBoolean ("Enabled", true);
                m_enabledBlockTeleportSeconds = config.GetBoolean ("AllowBlockTeleportsMinTime", true);
                secondsBeforeNextTeleport = config.GetFloat ("BlockTeleportsTime", 3);
                startDisabled = config.GetBoolean ("StartDisabled", startDisabled);
                forceLandingPointsOnCrossing = config.GetBoolean ("ForceLandingPointsOnCrossing", forceLandingPointsOnCrossing);
                m_checkMaturityLevel = config.GetBoolean ("CheckMaturityLevel", true);

                string banCriteriaString = config.GetString ("BanCriteria", "");
                if (banCriteriaString != "")
                    banCriteria = banCriteriaString.Split (',');
            }

            if (!m_enabled)
                return;

            regionConnector = Framework.Utilities.DataManager.RequestPlugin<IRegionConnector> ();

            m_scene = scene;
            scene.EventManager.OnNewClient += OnNewClient;
            scene.Permissions.OnAllowIncomingAgent += OnAllowedIncomingAgent;
            scene.Permissions.OnAllowedIncomingTeleport += OnAllowedIncomingTeleport;
            scene.EventManager.OnClosingClient += OnClosingClient;
            if (MainConsole.Instance != null) {
                MainConsole.Instance.Commands.AddCommand (
                    "set regionsetting maturity",
                    "set regionsetting maturity [value]",
                    "Sets a region's maturity - PG, Mature, Adult",
                    SetRegionMaturity, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "set regionsetting addestateban",
                    "set regionsetting addestateban [first] [last]",
                    "Add a user to the estate ban list",
                    SetRegionInfoOption, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "set regionsetting removeestateban",
                    "set regionsetting removeestateban [first] [last]",
                    "Remove a user from the estate ban list",
                    SetRegionInfoOption, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "set regionsetting addestatemanager",
                    "set regionsetting addestatemanager [first] [last]",
                    "Add a user to the estate manager list",
                    SetRegionInfoOption, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "set regionsetting removeestatemanager",
                    "set regionsetting removeestatemanager [first] [last]",
                    "Remove a user from the estate manager list",
                    SetRegionInfoOption, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "set regionsetting addestateaccess",
                    "set regionsetting addestateaccess [first] [last]",
                    "Add a user to the estate access list",
                    SetRegionInfoOption, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "set regionsetting removeestateaccess",
                    "set regionsetting removeestateaccess [first] [last]",
                    "Remove a user from the estate access list",
                    SetRegionInfoOption, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "estate ban user",
                    "estate ban user",
                    "Bans a user from the current estate",
                    BanUser, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "estate unban user",
                    "estate unban user",
                    "Bans a user from the current estate",
                    UnBanUser, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "login enable",
                    "login enable",
                    "Enable simulator logins",
                    ProcessLoginCommands, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "login disable",
                    "login disable",
                    "Disable simulator logins",
                    ProcessLoginCommands, true, true);

                MainConsole.Instance.Commands.AddCommand (
                    "login status",
                    "login status",
                    "Show login status",
                    ProcessLoginCommands, true, true);
            }
        }

        public void PostInitialise (IScene scene, IConfigSource source, ISimulationBase simBase)
        {
        }

        public void FinishStartup (IScene scene, IConfigSource source, ISimulationBase simBase)
        {
        }

        public void PostFinishStartup (IScene scene, IConfigSource source, ISimulationBase simBase)
        {
        }

        public void Close (IScene scene)
        {
            if (!m_enabled)
                return;

            m_scene = null;
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.Permissions.OnAllowIncomingAgent -= OnAllowedIncomingAgent;
            scene.Permissions.OnAllowedIncomingTeleport -= OnAllowedIncomingTeleport;
            scene.EventManager.OnClosingClient -= OnClosingClient;
        }

        public void DeleteRegion (IScene scene)
        {
        }

        public void StartupComplete ()
        {
            if (!startDisabled) {
                MainConsole.Instance.DebugFormat ("[Region]: Enabling logins");
                loginsDisabled = false;
            }
        }

        #endregion

        #region ISharedRegionModule

        public string Name {
            get { return "EstateSettingsModule"; }
        }

        #endregion

        #region Console Commands

        protected void ProcessLoginCommands (IScene scene, string [] cmd)
        {
            if (cmd.Length < 2) {
                MainConsole.Instance.Info ("Syntax: login enable|disable|status");
                return;
            }

            switch (cmd [1]) {
            case "enable":
                if (loginsDisabled)
                    MainConsole.Instance.Warn ("Enabling Logins");
                loginsDisabled = false;
                break;
            case "disable":
                if (!loginsDisabled)
                    MainConsole.Instance.Warn ("Disabling Logins");
                loginsDisabled = true;
                break;
            case "status":
                MainConsole.Instance.Warn ("Logins are currently " + (loginsDisabled ? "dis" : "en") + "abled.");
                break;
            default:
                MainConsole.Instance.Info ("Syntax: login enable|disable|status");
                break;
            }
        }

        protected void BanUser (IScene scene, string [] cmdparams)
        {
            string userName = MainConsole.Instance.Prompt ("User name:", "");
            IScenePresence SP = scene.SceneGraph.GetScenePresence (userName);
            if (SP == null) {
                MainConsole.Instance.Warn ("Could not find user");
                return;
            }

            string alert = MainConsole.Instance.Prompt ("Alert message:", "");
            EstateSettings ES = scene.RegionInfo.EstateSettings;
            AgentCircuitData circuitData = scene.AuthenticateHandler.GetAgentCircuitData (SP.UUID);

            ES.AddBan (new EstateBan {
                BannedHostAddress = circuitData.IPAddress,
                BannedHostIPMask = circuitData.IPAddress,
                BannedHostNameMask = circuitData.IPAddress,
                BannedUserID = SP.UUID,
                EstateID = ES.EstateID
            });

            Framework.Utilities.DataManager.RequestPlugin<IEstateConnector> ().SaveEstateSettings (ES);
            if (alert != "")
                SP.ControllingClient.Kick (alert);
            else
                SP.ControllingClient.Kick ("\nThe WhiteCore manager banned and kicked you out.\n");

            // kick client...
            IEntityTransferModule transferModule = SP.Scene.RequestModuleInterface<IEntityTransferModule> ();
            if (transferModule != null)
                transferModule.IncomingCloseAgent (SP.Scene, SP.UUID);
        }

        protected void UnBanUser (IScene scene, string [] cmdparams)
        {
            string userName = MainConsole.Instance.Prompt ("User name:", "");
            UserAccount account = scene.UserAccountService.GetUserAccount (null, userName);
            if (account == null) {
                MainConsole.Instance.Warn ("Could not find user");
                return;
            }

            EstateSettings ES = scene.RegionInfo.EstateSettings;
            ES.RemoveBan (account.PrincipalID);
            Framework.Utilities.DataManager.RequestPlugin<IEstateConnector> ().
                SaveEstateSettings (ES);
        }

        protected void SetRegionMaturity (IScene scene, string [] cmdparams)
        {
            if (MainConsole.Instance.ConsoleScene == null) {
                MainConsole.Instance.Info ("[Regionsettings]: This command requires a region to be selected\n          Please change to a region first");
                return;
            }

            string maturitylevel = "";

            if (cmdparams.Length < 4) {
                maturitylevel = MainConsole.Instance.Prompt ("Which maturity level? (PG/Mature/Adult)", maturitylevel);
                if (maturitylevel == "")
                    return;
             } else
                maturitylevel = cmdparams [3];

            maturitylevel = maturitylevel.ToLower ();
            switch (maturitylevel) {
            case "pg":
                m_scene.RegionInfo.AccessLevel = Util.ConvertMaturityToAccessLevel (0);
                break;
            case "mature":
                m_scene.RegionInfo.AccessLevel = Util.ConvertMaturityToAccessLevel (1);
                break;
            case "adult":
                m_scene.RegionInfo.AccessLevel = Util.ConvertMaturityToAccessLevel (2);
                break;
            default:
                MainConsole.Instance.Warn (
                    "Your parameter did not match any existing parameters. Try PG, Mature, or Adult");
                return;

            }

            //Tell the grid about the changes
            IGridRegisterModule gridRegModule = m_scene.RequestModuleInterface<IGridRegisterModule> ();
            if (gridRegModule != null)
                gridRegModule.UpdateGridRegion (m_scene);

        }

        protected void SetRegionInfoOption (IScene scene, string [] cmdparams)
        {
            if (MainConsole.Instance.ConsoleScene == null) {
                MainConsole.Instance.Info ("[Regionsettings]: This command requires a region to be selected\n          Please change to a region first");
                return;
            }
             
            var setcmd = cmdparams [2];
            var firstname = "";
            var lastname = "";

            if (cmdparams.Length < 5) {
                string name = "";
                name = MainConsole.Instance.Prompt ("User Name <first last>: ", name);
                if (name == "")
                    return;
                var names = name.Split (' ');
                if (names.Length < 2)
                    return;
                firstname = names [0];
                lastname = names [1];
            } else {
                firstname = cmdparams [3];
                lastname = cmdparams [4];
            }


            var account = m_scene.UserAccountService.GetUserAccount (null, firstname, lastname);
            if (account != null) {
                var userID = account.PrincipalID;

                if (setcmd == "AddEstateBan".ToLower ()) {
                    EstateBan EB = new EstateBan { BannedUserID = userID };
                    m_scene.RegionInfo.EstateSettings.AddBan (EB);
                }
                if (setcmd == "AddEstateManager".ToLower ())
                    m_scene.RegionInfo.EstateSettings.AddEstateManager (userID);

                if (setcmd == "AddEstateAccess".ToLower ())
                    m_scene.RegionInfo.EstateSettings.AddEstateUser (userID);

                if (setcmd == "RemoveEstateBan".ToLower ())
                    m_scene.RegionInfo.EstateSettings.RemoveBan (userID);

                if (setcmd == "RemoveEstateManager".ToLower ())
                    m_scene.RegionInfo.EstateSettings.RemoveEstateManager (userID);

                if (setcmd == "RemoveEstateAccess".ToLower ())
                    m_scene.RegionInfo.EstateSettings.RemoveEstateUser (userID);

                Framework.Utilities.DataManager.RequestPlugin<IEstateConnector> ()
                         .SaveEstateSettings (m_scene.RegionInfo.EstateSettings);
            } else
                MainConsole.Instance.Warn("[Regionsettings]: Unable to determine user account details");

        }

        #endregion

        #region Client

        void OnNewClient (IClientAPI client)
        {
            client.OnGodlikeMessage += GodlikeMessage;
            client.OnEstateTelehubRequest += GodlikeMessage;
            //This is ok, we do estate checks and check to make sure that only telehubs are dealt with here
        }

        void OnClosingClient (IClientAPI client)
        {
            client.OnGodlikeMessage -= GodlikeMessage;
            client.OnEstateTelehubRequest -= GodlikeMessage;
        }

        #endregion

        #region Telehub Settings

        public void GodlikeMessage (IClientAPI client, UUID requester, string Method, List<string> Parameters)
        {
            if (regionConnector == null)
                return;
            IScenePresence Sp = client.Scene.GetScenePresence (client.AgentId);
            if (!client.Scene.Permissions.CanIssueEstateCommand (client.AgentId, false))
                return;

            string parameter1 = Parameters [0];
            if (Method == "telehub") {
                if (parameter1 == "spawnpoint remove") {
                    Telehub telehub = regionConnector.FindTelehub (client.Scene.RegionInfo.RegionID,
                                          client.Scene.RegionInfo.RegionHandle);
                    if (telehub == null)
                        return;

                    //Remove the one we sent at X
                    telehub.SpawnPos.RemoveAt (int.Parse (Parameters [1]));
                    regionConnector.AddTelehub (telehub, client.Scene.RegionInfo.RegionHandle);
                    client.Scene.RegionInfo.RegionSettings.TeleHub = telehub;

                    SendTelehubInfo (client);
                }

                if (parameter1 == "spawnpoint add") {
                    ISceneChildEntity part = Sp.Scene.GetSceneObjectPart (uint.Parse (Parameters [1]));
                    if (part == null)
                        return;

                    Telehub telehub = regionConnector.FindTelehub (client.Scene.RegionInfo.RegionID,
                                          client.Scene.RegionInfo.RegionHandle);
                    if (telehub == null)
                        return;

                    telehub.RegionLocX = client.Scene.RegionInfo.RegionLocX;
                    telehub.RegionLocY = client.Scene.RegionInfo.RegionLocY;
                    telehub.RegionID = client.Scene.RegionInfo.RegionID;
                    Vector3 pos = new Vector3 (telehub.TelehubLocX, telehub.TelehubLocY, telehub.TelehubLocZ);
                    if (telehub.TelehubLocX == 0 && telehub.TelehubLocY == 0)
                        return; //No spawns without a telehub

                    telehub.SpawnPos.Add (part.AbsolutePosition - pos); //Spawns are offsets
                    regionConnector.AddTelehub (telehub, client.Scene.RegionInfo.RegionHandle);
                    client.Scene.RegionInfo.RegionSettings.TeleHub = telehub;

                    SendTelehubInfo (client);
                }

                if (parameter1 == "delete") {
                    regionConnector.RemoveTelehub (client.Scene.RegionInfo.RegionID, client.Scene.RegionInfo.RegionHandle);
                    client.Scene.RegionInfo.RegionSettings.TeleHub = new Telehub ();

                    SendTelehubInfo (client);
                }

                if (parameter1 == "connect") {
                    ISceneChildEntity part = Sp.Scene.GetSceneObjectPart (uint.Parse (Parameters [1]));
                    if (part == null)
                        return;

                    Telehub telehub = regionConnector.FindTelehub (client.Scene.RegionInfo.RegionID,
                                          client.Scene.RegionInfo.RegionHandle);
                    if (telehub == null)
                        telehub = new Telehub ();
                    telehub.RegionLocX = client.Scene.RegionInfo.RegionLocX;
                    telehub.RegionLocY = client.Scene.RegionInfo.RegionLocY;
                    telehub.RegionID = client.Scene.RegionInfo.RegionID;
                    telehub.TelehubLocX = part.AbsolutePosition.X;
                    telehub.TelehubLocY = part.AbsolutePosition.Y;
                    telehub.TelehubLocZ = part.AbsolutePosition.Z;
                    telehub.TelehubRotX = part.ParentEntity.Rotation.X;
                    telehub.TelehubRotY = part.ParentEntity.Rotation.Y;
                    telehub.TelehubRotZ = part.ParentEntity.Rotation.Z;
                    telehub.ObjectUUID = part.UUID;
                    telehub.Name = part.Name;
                    regionConnector.AddTelehub (telehub, client.Scene.RegionInfo.RegionHandle);
                    client.Scene.RegionInfo.RegionSettings.TeleHub = telehub;

                    SendTelehubInfo (client);
                }

                if (parameter1 == "info ui")
                    SendTelehubInfo (client);
            }
        }

        void SendTelehubInfo (IClientAPI client)
        {
            if (regionConnector != null) {
                Telehub telehub = regionConnector.FindTelehub (client.Scene.RegionInfo.RegionID,
                                      client.Scene.RegionInfo.RegionHandle);
                if (telehub == null) {
                    client.SendTelehubInfo (Vector3.Zero, Quaternion.Identity, new List<Vector3> (), UUID.Zero, "");
                } else {
                    Vector3 pos = new Vector3 (telehub.TelehubLocX, telehub.TelehubLocY, telehub.TelehubLocZ);
                    Quaternion rot = new Quaternion (telehub.TelehubRotX, telehub.TelehubRotY, telehub.TelehubRotZ);
                    client.SendTelehubInfo (pos, rot, telehub.SpawnPos, telehub.ObjectUUID, telehub.Name);
                }
            }
        }

        #endregion

        #region Teleport Permissions

        bool OnAllowedIncomingTeleport (UUID userID, IScene scene, Vector3 position, uint teleportFlags,
                                       out Vector3 newPosition, out string reason)
        {
            newPosition = position;
            UserAccount account = scene.UserAccountService.GetUserAccount (scene.RegionInfo.AllScopeIDs, userID);

            IScenePresence Sp = scene.GetScenePresence (userID);
            if (account == null) {
                reason = "Failed authentication.";
                return false; //NO!
            }


            //Make sure that this user is inside the region as well
            if (position.X < -2f || position.Y < -2f ||
                position.X > scene.RegionInfo.RegionSizeX - 2 || position.Y > scene.RegionInfo.RegionSizeY - 2) {
                MainConsole.Instance.DebugFormat (
                    "[Estate Service]: AllowedIncomingTeleport was given an illegal position of {0} for avatar {1}, {2}. Clamping",
                    position, Name, userID);

                bool changedX = false;
                bool changedY = false;
                while (position.X < 0) {
                    position.X += scene.RegionInfo.RegionSizeX;
                    changedX = true;
                }
                while (position.X > scene.RegionInfo.RegionSizeX) {
                    position.X -= scene.RegionInfo.RegionSizeX;
                    changedX = true;
                }

                while (position.Y < 0) {
                    position.Y += scene.RegionInfo.RegionSizeY;
                    changedY = true;
                }
                while (position.Y > scene.RegionInfo.RegionSizeY) {
                    position.Y -= scene.RegionInfo.RegionSizeY;
                    changedY = true;
                }

                if (changedX)
                    position.X = scene.RegionInfo.RegionSizeX - position.X;
                if (changedY)
                    position.Y = scene.RegionInfo.RegionSizeY - position.Y;
            }

            IAgentConnector agentConnector = Framework.Utilities.DataManager.RequestPlugin<IAgentConnector> ();
            IAgentInfo agentInfo = null;
            if (agentConnector != null)
                agentInfo = agentConnector.GetAgent (userID);

            ILandObject ILO = null;
            IParcelManagementModule parcelManagement = scene.RequestModuleInterface<IParcelManagementModule> ();
            if (parcelManagement != null) {
                ILO = parcelManagement.GetLandObject (position.X, position.Y);

                if (ILO == null) {
                    if (Sp != null)
                        Sp.ClearSavedVelocity (); //If we are moving the agent, clear their velocity
                    //Can't find land, give them the first parcel in the region and find a good position for them
                    ILO = parcelManagement.AllParcels () [0];
                    position = parcelManagement.GetParcelCenterAtGround (ILO);
                }

                //parcel permissions
                if (ILO.IsBannedFromLand (userID)) //Note: restricted is dealt with in the next block
                {
                    if (Sp != null)
                        Sp.ClearSavedVelocity (); //If we are moving the agent, clear their velocity
                    if (Sp == null) {
                        reason = "Banned from this parcel.";
                        return false;
                    }

                    if (!FindUnBannedParcel (position, Sp, userID, out ILO, out newPosition, out reason)) {
                        //We found a place for them, but we don't need to check any further on positions here
                        //return true;
                    }
                }
                //Move them out of banned parcels
                ParcelFlags parcelflags = (ParcelFlags)ILO.LandData.Flags;
                if ((parcelflags & ParcelFlags.UseAccessGroup) == ParcelFlags.UseAccessGroup &&
                    (parcelflags & ParcelFlags.UseAccessList) == ParcelFlags.UseAccessList &&
                    (parcelflags & ParcelFlags.UsePassList) == ParcelFlags.UsePassList) {
                    if (Sp != null)
                        Sp.ClearSavedVelocity (); //If we are moving the agent, clear their velocity
                                                  //One of these is in play then
                    if ((parcelflags & ParcelFlags.UseAccessGroup) == ParcelFlags.UseAccessGroup) {
                        if (Sp == null) {
                            reason = "Banned from this parcel.";
                            return false;
                        }
                        if (Sp.ControllingClient.ActiveGroupId != ILO.LandData.GroupID) {
                            if (!FindUnBannedParcel (position, Sp, userID, out ILO, out newPosition, out reason)) {
                                //We found a place for them, but we don't need to check any further on positions here
                                //return true;
                            }
                        }
                    } else if ((parcelflags & ParcelFlags.UseAccessList) == ParcelFlags.UseAccessList) {
                        if (Sp == null) {
                            reason = "Banned from this parcel.";
                            return false;
                        }
                        //All but the people on the access list are banned
                        if (ILO.IsRestrictedFromLand (userID))
                            if (!FindUnBannedParcel (position, Sp, userID, out ILO, out newPosition, out reason)) {
                                //We found a place for them, but we don't need to check any further on positions here
                                //return true;
                            }
                    } else if ((parcelflags & ParcelFlags.UsePassList) == ParcelFlags.UsePassList) {
                        if (Sp == null) {
                            reason = "Banned from this parcel.";
                            return false;
                        }
                        //All but the people on the pass/access list are banned
                        if (ILO.IsRestrictedFromLand (Sp.UUID))
                            if (!FindUnBannedParcel (position, Sp, userID, out ILO, out newPosition, out reason)) {
                                //We found a place for them, but we don't need to check any further on positions here
                                //return true;
                            }
                    }

                }
            }

            // fairly unlikely but you never know...
            if (ILO == null) {
                reason = "Unable to find land details";
                return false;
            }

            EstateSettings ES = scene.RegionInfo.EstateSettings;
            TeleportFlags tpflags = (TeleportFlags)teleportFlags;
            const TeleportFlags allowableFlags =
                             TeleportFlags.ViaLandmark |
                             TeleportFlags.ViaHome |
                             TeleportFlags.ViaLure |
                             TeleportFlags.ForceRedirect |
                             TeleportFlags.Godlike |
                             TeleportFlags.NineOneOne;

            //If the user wants to force landing points on crossing, we act like they are not crossing, otherwise, check the child property and that the ViaRegionID is set
            bool isCrossing = !forceLandingPointsOnCrossing && (Sp != null && Sp.IsChildAgent &&
                              ((tpflags & TeleportFlags.ViaRegionID) == TeleportFlags.ViaRegionID));
            bool directTeleport = (tpflags & allowableFlags) != 0;

            // If the estate does not allow direct teleporting, move them to the nearest landing point
            if (!directTeleport && !isCrossing && !ES.AllowDirectTeleport) {
                if (Sp != null)
                    Sp.ClearSavedVelocity (); //If we are moving the agent, clear their velocity
                if (!scene.Permissions.IsGod (userID)) {
                    Telehub telehub = regionConnector.FindTelehub (scene.RegionInfo.RegionID,
                                          scene.RegionInfo.RegionHandle);
                    if (telehub != null) {
                        if (telehub.SpawnPos.Count == 0) {
                            newPosition = new Vector3 (telehub.TelehubLocX, telehub.TelehubLocY, telehub.TelehubLocZ);
                        } else {
                            int LastTelehubNum = 0;
                            if (!lastTelehub.TryGetValue (scene.RegionInfo.RegionID, out LastTelehubNum))
                                LastTelehubNum = 0;
                            newPosition = telehub.SpawnPos [LastTelehubNum] +
                                                 new Vector3 (telehub.TelehubLocX, telehub.TelehubLocY, telehub.TelehubLocZ);
                            LastTelehubNum++;
                            if (LastTelehubNum == telehub.SpawnPos.Count)
                                LastTelehubNum = 0;
                            lastTelehub [scene.RegionInfo.RegionID] = LastTelehubNum;
                        }
                    } else if (ILO.LandData.LandingType == (int)LandingType.LandingPoint) // we have a landing point specified, use it
                        newPosition = ILO.LandData.UserLocation != Vector3.Zero
                                          ? ILO.LandData.UserLocation
                                          : parcelManagement.GetNearestRegionEdgePosition (Sp);

                }
            } else if (!directTeleport && !isCrossing && 
                       !scene.Permissions.GenericParcelPermission (userID, ILO, (ulong)GroupPowers.None)) {
                // Estate allows direct teleporting 
                if (Sp != null)
                    Sp.ClearSavedVelocity (); //If we are moving the agent, clear their velocity

                if (ILO.LandData.LandingType == (int)LandingType.None) {
                    //Blocked, force this person off this land. Find a new parcel for them
                    List<ILandObject> Parcels = parcelManagement.ParcelsNearPoint (position);
                    if (Parcels.Count > 1) {
                        newPosition = parcelManagement.GetNearestRegionEdgePosition (Sp);
                    } else {
                        bool found = false;
                        //We need to check here as well for bans, can't toss someone into a parcel they are banned from
                        foreach (ILandObject tpParcel in Parcels.Where (Parcel => !Parcel.IsBannedFromLand (userID))) {
                            //Now we have to check their userloc
                            if (ILO.LandData.LandingType == (int)LandingType.None)
                                continue; //Blocked, check next one

                            if (ILO.LandData.LandingType == (int)LandingType.LandingPoint) //Use their landing spot if set
                                newPosition = tpParcel.LandData.UserLocation != Vector3.Zero
                                                 ? tpParcel.LandData.UserLocation
                                                 : parcelManagement.GetParcelCenterAtGround (tpParcel);

                            else //They allow for anywhere, so dump them in the center at the ground
                                newPosition = parcelManagement.GetParcelCenterAtGround (tpParcel);

                            found = true;
                        }

                        if (!found) //Dump them at the edge
                        {
                            if (Sp != null)
                                newPosition = parcelManagement.GetNearestRegionEdgePosition (Sp);
                            else {
                                reason = "Banned from this parcel.";
                                return false;
                            }
                        }
                    }
                } else if (ILO.LandData.LandingType == (int)LandingType.LandingPoint) {
                    // crossing regions or a directed teleport so move to landing spot if set
                    newPosition = ILO.LandData.UserLocation != Vector3.Zero
                                      ? ILO.LandData.UserLocation
                                      : parcelManagement.GetNearestRegionEdgePosition (Sp);
                }
            }

            //We assume that our own region isn't null....
            if (agentInfo != null) {
                //Can only enter prelude regions once!
                if (scene.RegionInfo.RegionFlags != -1 &&
                    ((scene.RegionInfo.RegionFlags & (int)RegionFlags.Prelude) == (int)RegionFlags.Prelude) &&
                    agentInfo != null) {
                    if (agentInfo.OtherAgentInformation.ContainsKey ("Prelude" + scene.RegionInfo.RegionID)) {
                        reason = "You may not enter this region as you have already been to this prelude region.";
                        return false;
                    } else {
                        agentInfo.OtherAgentInformation.Add ("Prelude" + scene.RegionInfo.RegionID,
                            OSD.FromInteger ((int)IAgentFlags.PastPrelude));
                        agentConnector.UpdateAgent (agentInfo);
                    }
                }
                if (agentInfo.OtherAgentInformation.ContainsKey ("LimitedToEstate")) {
                    int limitedToEstate = agentInfo.OtherAgentInformation ["LimitedToEstate"];
                    if (scene.RegionInfo.EstateSettings.EstateID != limitedToEstate) {
                        reason = "You may not enter this reason, as it is outside of the estate you are limited to.";
                        return false;
                    }
                }
            }


            if ((ILO.LandData.Flags & (int)ParcelFlags.DenyAnonymous) != 0) {
                if (account != null &&
                    (account.UserFlags & (int)IUserProfileInfo.ProfileFlags.NoPaymentInfoOnFile) ==
                    (int)IUserProfileInfo.ProfileFlags.NoPaymentInfoOnFile) {
                    reason = "You may not enter this region.";
                    return false;
                }
            }

            if ((ILO.LandData.Flags & (uint)ParcelFlags.DenyAgeUnverified) != 0 && agentInfo != null) {
                if ((agentInfo.Flags & IAgentFlags.Minor) == IAgentFlags.Minor) {
                    reason = "You may not enter this region.";
                    return false;
                }
            }

            //Check that we are not underground as well
            ITerrainChannel chan = scene.RequestModuleInterface<ITerrainChannel> ();
            if (chan != null) {
                float posZLimit = chan [(int)newPosition.X, (int)newPosition.Y] + (float)1.25;

                if (posZLimit >= (newPosition.Z) && !(float.IsInfinity (posZLimit) || float.IsNaN (posZLimit))) {
                    newPosition.Z = posZLimit;
                }
            }

            reason = "";
            return true;
        }

        bool OnAllowedIncomingAgent (IScene scene, AgentCircuitData agent, bool isRootAgent, out string reason)
        {
            #region Incoming Agent Checks

            UserAccount account = scene.UserAccountService.GetUserAccount (scene.RegionInfo.AllScopeIDs, agent.AgentID);
            if (account == null) {
                reason = "No account exists";
                return false;
            }
            IScenePresence Sp = scene.GetScenePresence (agent.AgentID);

            if (loginsDisabled) {
                reason = "Logins are currently Disabled";
                return false;
            }

            //Check how long its been since the last TP
            if (m_enabledBlockTeleportSeconds && Sp != null && !Sp.IsChildAgent) {
                if (timeSinceLastTeleport.ContainsKey (Sp.Scene.RegionInfo.RegionID)) {
                    if (timeSinceLastTeleport [Sp.Scene.RegionInfo.RegionID] > Util.UnixTimeSinceEpoch ()) {
                        reason = "Too many teleports. Please try again soon.";
                        return false; // Too soon since the last TP
                    }
                }
                timeSinceLastTeleport [Sp.Scene.RegionInfo.RegionID] = Util.UnixTimeSinceEpoch () +
                ((int)(secondsBeforeNextTeleport));
            }

            //Gods tp freely
            if ((Sp != null && Sp.GodLevel != 0) || (account != null && account.UserLevel != 0)) {
                reason = "";
                return true;
            }

            //Check whether they fit any ban criteria
            if (Sp != null) {
                foreach (string banstr in banCriteria) {
                    if (Sp.Name.Contains (banstr)) {
                        reason = "You have been banned from this region.";
                        return false;
                    } else if (((IPEndPoint)Sp.ControllingClient.GetClientEP ()).Address.ToString ().Contains (banstr)) {
                        reason = "You have been banned from this region.";
                        return false;
                    }
                }
                //Make sure they exist in the grid right now
                IAgentInfoService presence = scene.RequestModuleInterface<IAgentInfoService> ();
                if (presence == null) {
                    reason = string.Format (
                        "Failed to verify user presence in the grid for {0} in region {1}. Presence service does not exist.",
                        account.Name, scene.RegionInfo.RegionName);
                    return false;
                }

                UserInfo pinfo = presence.GetUserInfo (agent.AgentID.ToString ());

                if (pinfo == null || (!pinfo.IsOnline && ((agent.TeleportFlags & (uint)TeleportFlags.ViaLogin) == 0))) {
                    reason = string.Format (
                        "Failed to verify user presence in the grid for {0}, access denied to region {1}.",
                        account.Name, scene.RegionInfo.RegionName);
                    return false;
                }
            }

            EstateSettings ES = scene.RegionInfo.EstateSettings;

            IEntityCountModule entityCountModule = scene.RequestModuleInterface<IEntityCountModule> ();
            if (entityCountModule != null && scene.RegionInfo.RegionSettings.AgentLimit
                < entityCountModule.RootAgents + 1 &&
                scene.RegionInfo.RegionSettings.AgentLimit > 0) {
                reason = "Too many agents at this time. Please come back later.";
                return false;
            }

            List<EstateBan> EstateBans = new List<EstateBan> (ES.EstateBans);
            int i = 0;
            //Check bans
            foreach (EstateBan ban in EstateBans) {
                if (ban.BannedUserID == agent.AgentID) {
                    if (Sp != null) {
                        string banIP = ((IPEndPoint)Sp.ControllingClient.GetClientEP ()).Address.ToString ();

                        if (ban.BannedHostIPMask != banIP) //If it changed, ban them again
                        {
                            //Add the ban with the new hostname
                            ES.AddBan (new EstateBan {
                                BannedHostIPMask = banIP,
                                BannedUserID = ban.BannedUserID,
                                EstateID = ban.EstateID,
                                BannedHostAddress = ban.BannedHostAddress,
                                BannedHostNameMask = ban.BannedHostNameMask
                            });
                            //Update the database
                            Framework.Utilities.DataManager.RequestPlugin<IEstateConnector> ().SaveEstateSettings (ES);
                        }
                    }

                    reason = "Banned from this region.";
                    return false;
                }
                if (Sp != null) {
                    bool banendpoint = false;
                    IPAddress endpoint = Sp.ControllingClient.EndPoint;
                    IPHostEntry rDNS = null;
                    if (endpoint != null) {
                        try {
                            rDNS = Dns.GetHostEntry (endpoint);
                        } catch (SocketException) {
                            MainConsole.Instance.WarnFormat ("[IP Ban] IP address \"{0}\" cannot be resolved via DNS", endpoint);
                            rDNS = null;
                        }
                        if (rDNS != null)
                            banendpoint = rDNS.HostName.Contains (ban.BannedHostIPMask);
                        if (!banendpoint)
                            banendpoint = endpoint.ToString ().StartsWith (ban.BannedHostIPMask, StringComparison.Ordinal);
                    }
                    if (ban.BannedHostIPMask == agent.IPAddress || banendpoint) {
                        //Ban the new user
                        ES.AddBan (new EstateBan {
                            EstateID = ES.EstateID,
                            BannedHostIPMask = agent.IPAddress,
                            BannedUserID = agent.AgentID,
                            BannedHostAddress = agent.IPAddress,
                            BannedHostNameMask = agent.IPAddress
                        });
                        Framework.Utilities.DataManager.RequestPlugin<IEstateConnector> ().
                            SaveEstateSettings (ES);

                        reason = "Banned from this region.";
                        return false;
                    }
                }
                i++;
            }

            //Estate owners/managers/access list people/access groups tp freely as well
            if (ES.EstateOwner == agent.AgentID ||
                new List<UUID> (ES.EstateManagers).Contains (agent.AgentID) ||
                new List<UUID> (ES.EstateAccess).Contains (agent.AgentID) ||
                CheckEstateGroups (ES, agent)) {
                reason = "";
                return true;
            }

            if (ES.DenyAnonymous &&
                ((account.UserFlags & (int)IUserProfileInfo.ProfileFlags.NoPaymentInfoOnFile) ==
                (int)IUserProfileInfo.ProfileFlags.NoPaymentInfoOnFile)) {
                reason = "You may not enter this region.";
                return false;
            }

            if (ES.DenyIdentified &&
                ((account.UserFlags & (int)IUserProfileInfo.ProfileFlags.PaymentInfoOnFile) ==
                (int)IUserProfileInfo.ProfileFlags.PaymentInfoOnFile)) {
                reason = "You may not enter this region.";
                return false;
            }

            if (ES.DenyTransacted &&
                ((account.UserFlags & (int)IUserProfileInfo.ProfileFlags.PaymentInfoInUse) ==
                (int)IUserProfileInfo.ProfileFlags.PaymentInfoInUse)) {
                reason = "You may not enter this region.";
                return false;
            }

            const long m_Day = 24 * 60 * 60; //Find out day length in seconds
            if (scene.RegionInfo.RegionSettings.MinimumAge != 0 &&
                (account.Created - Util.UnixTimeSinceEpoch ()) < (scene.RegionInfo.RegionSettings.MinimumAge * m_Day)) {
                reason = "You may not enter this region.";
                return false;
            }

            if (!ES.PublicAccess) {
                reason = "You may not enter this region, Public access has been turned off.";
                return false;
            }

            IAgentConnector agentConnector = Framework.Utilities.DataManager.RequestPlugin<IAgentConnector> ();
            IAgentInfo agentInfo = null;
            if (agentConnector != null) {
                agentInfo = agentConnector.GetAgent (agent.AgentID);
                if (agentInfo == null) {
                    agentConnector.CreateNewAgent (agent.AgentID);
                    agentInfo = agentConnector.GetAgent (agent.AgentID);
                }
            }

            if (m_checkMaturityLevel) {
                if (agentInfo != null &&
                    scene.RegionInfo.AccessLevel > Util.ConvertMaturityToAccessLevel ((uint)agentInfo.MaturityRating)) {
                    reason = "The region has too high of a maturity level. Blocking teleport.";
                    return false;
                }

                if (agentInfo != null && ES.DenyMinors && (agentInfo.Flags & IAgentFlags.Minor) == IAgentFlags.Minor) {
                    reason = "The region has too high of a maturity level. Blocking teleport.";
                    return false;
                }
            }

            #endregion

            reason = "";
            return true;
        }

        bool CheckEstateGroups (EstateSettings ES, AgentCircuitData agent)
        {
            IGroupsModule gm = m_scene.RequestModuleInterface<IGroupsModule> ();
            if (gm != null && ES.EstateGroups.Count > 0) {
                GroupMembershipData [] gmds = gm.GetMembershipData (agent.AgentID);
                return gmds.Any (gmd => ES.EstateGroups.Contains (gmd.GroupID));
            }
            return false;
        }

        bool FindUnBannedParcel (Vector3 Position, IScenePresence Sp, UUID AgentID, out ILandObject ILO,
                                out Vector3 newPosition, out string reason)
        {
            ILO = null;
            IParcelManagementModule parcelManagement = Sp.Scene.RequestModuleInterface<IParcelManagementModule> ();
            if (parcelManagement != null) {
                List<ILandObject> Parcels = parcelManagement.ParcelsNearPoint (Position);
                if (Parcels.Count == 0) {
                    newPosition = parcelManagement.GetNearestRegionEdgePosition (Sp);
                    ILO = null;

                    //Dumped in the region corner, we will leave them there
                    reason = "";
                    return false;
                }

                // we have some parcels to check
                bool FoundParcel = false;

                foreach (ILandObject lo in Parcels.Where (lo => !lo.IsEitherBannedOrRestricted (AgentID))) {
                    newPosition = lo.LandData.UserLocation;
                    ILO = lo; //Update the parcel settings
                    FoundParcel = true;
                    break;
                }

                if (!FoundParcel) {
                    //Dump them in the region corner as they are banned from all nearby parcels
                    newPosition = parcelManagement.GetNearestRegionEdgePosition (Sp);
                    reason = "";
                    ILO = null;
                    return false;
                }

            }
            newPosition = Position;
            reason = "";
            return true;
        }

        #endregion

    }
}
