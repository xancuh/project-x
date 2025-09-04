function start(placeId, port, url)
	--[[
		Sam's 2023 Gameserver made from Fobe 2021 gameserver
		Slightly modified from ROBLOX https://web.archive.org/web/20160714155618/http://roblox.com/Game/GameServer.ashx
	--]]
	
		warn("[DBG] : Gameserver.lua is starting...")
		local domain = url;
		local baseurl = url;
		local creatorid = 1;
		local isDebugServer = false;
		local ns = game:GetService("NetworkServer")
		local api = baseurl;
		local assetgame = baseurl;
		local http = game:GetService("HttpService")
		http.HttpEnabled = true
	
		print('enabled http')
	
	
		------------------- UTILITY FUNCTIONS --------------------------
	
		local function waitForChild(parent, childName)
			while true do
				local child = parent:findFirstChild(childName)
				if child then
					return child
				end
				parent.ChildAdded:wait()
			end
		end
	
		local function sendKillJobSignal()
			pcall(function()
				game:HttpPost(domain .. "/gs/shutdown", http:JSONEncode({
					["authorization"] = "sexybeast69420",
					["serverId"] = game.JobId,
					["placeId"] = placeId,
				}), false, "application/json");
			end)
			pcall(function()
				ns:Stop()
			end)
		end
	
		local function sendIsAliveSignal()
			pcall(function()
				game:HttpPost(domain .. "/gs/ping", http:JSONEncode({
					["authorization"] = "sexybeast69420",
					["serverId"] = game.JobId,
					["placeId"] = placeId,
				}), false, "application/json");
			end)
		end
	
		local function reportPlayerEvent(userId, t)
			-- wrapped in pcall to prevent keys spilling in error logs
			local ok, msg = pcall(function()
				local msg = http:JSONEncode({
					["authorization"] = "sexybeast69420",
					["serverId"] = game.JobId,
					["userId"] = tostring(userId),
					["eventType"] = t,
					["placeId"] = tostring(placeId),
				})
				print("sending",msg)
				game:HttpPost(domain .. "/gs/players/report", msg, false, "application/json");
			end)
			print("player event",ok,msg)
		end
	
		local function isAlive()
			while true do
				sendIsAliveSignal()
				wait(30) --ping every 30 seconds (subject to change?)
			end
		end
	
		-----------------------------------END UTILITY FUNCTIONS -------------------------
	
		-----------------------------------"CUSTOM" SHARED CODE----------------------------------
	
		pcall(function() settings().Network.UseInstancePacketCache = true end)
		pcall(function() settings().Network.UsePhysicsPacketCache = true end)
		pcall(function() settings()["Task Scheduler"].PriorityMethod = Enum.PriorityMethod.AccumulatedError end)
	
		settings().Network.PhysicsSend = Enum.PhysicsSendMethod.TopNErrors
		settings().Network.ExperimentalPhysicsEnabled = true
		settings().Network.WaitingForCharacterLogRate = 100
		settings().Diagnostics.LuaRamLimit = 0
		pcall(function() settings().Diagnostics:LegacyScriptMode() end)
	
		-----------------------------------START GAME SHARED SCRIPT------------------------------
	
		-- initialize --
		local scriptContext = game:GetService('ScriptContext')
		local _, error = pcall(function() 
			warn('Parsing ScriptContext StarterScript Id 37801172')
			-- Creates all neccessary scripts for the gui on initial load, everything except build tools
			-- Created by Ben T. 10/29/10
			-- Please note that these are loaded in a specific order to diminish errors/perceived load time by user
	
			local scriptContext = game:GetService("ScriptContext")
			local touchEnabled = game:GetService("UserInputService").TouchEnabled
	
			local RobloxGui = game:GetService("CoreGui"):WaitForChild("RobloxGui")
	
			local soundFolder = Instance.new("Folder")
			soundFolder.Name = "Sounds"
			soundFolder.Parent = RobloxGui
	
			-- TopBar
			scriptContext:AddCoreScriptLocal("CoreScripts/Topbar", RobloxGui)
	
			-- SettingsScript
			local luaControlsSuccess, luaControlsFlagValue = pcall(function() return settings():GetFFlag("UseLuaCameraAndControl") end)
	
			-- MainBotChatScript (the Lua part of Dialogs)
			--scriptContext:AddCoreScriptLocal("CoreScripts/MainBotChatScript2", RobloxGui)
	
			-- Developer Console Script
			--scriptContext:AddCoreScriptLocal("CoreScripts/DeveloperConsole", RobloxGui)
	
			-- In-game notifications script
			scriptContext:AddCoreScriptLocal("CoreScripts/NotificationScript2", RobloxGui)
	
			-- Chat script
			spawn(function() require(RobloxGui.Modules.Chat) end)
			spawn(function() require(RobloxGui.Modules.PlayerlistModule) end)
	
			local luaBubbleChatSuccess, luaBubbleChatFlagValue = pcall(function() return settings():GetFFlag("LuaBasedBubbleChat") end)
			if luaBubbleChatSuccess and luaBubbleChatFlagValue then
				scriptContext:AddCoreScriptLocal("CoreScripts/BubbleChat", RobloxGui)
			end
	
			-- Purchase Prompt Script
			scriptContext:AddCoreScriptLocal("CoreScripts/PurchasePromptScript2", RobloxGui)
	
			-- Health Script
			scriptContext:AddCoreScriptLocal("CoreScripts/HealthScript", RobloxGui)
	
			do -- Backpack!
				spawn(function() require(RobloxGui.Modules.BackpackScript) end)
			end
	
			scriptContext:AddCoreScriptLocal("CoreScripts/VehicleHud", RobloxGui)
	
			--scriptContext:AddCoreScriptLocal("CoreScripts/GamepadMenu", RobloxGui)
	
			if touchEnabled then -- touch devices don't use same control frame
				-- only used for touch device button generation
				scriptContext:AddCoreScriptLocal("CoreScripts/ContextActionTouch", RobloxGui)
	
				RobloxGui:WaitForChild("ControlFrame")
				RobloxGui.ControlFrame:WaitForChild("BottomLeftControl")
				RobloxGui.ControlFrame.BottomLeftControl.Visible = false
			end
	
			warn('Parsed Successfully')
		end)
	
		if error then 
			warn('Unable to parse ScriptContext StarteScript Id 37801172')
			warn(error)
		end
	
		scriptContext.ScriptsDisabled = true
	
		-- general datamodel settings --
		game:SetPlaceID(placeId, false)
		game:SetCreatorID(creatorid, Enum.CreatorType.User)
		game:GetService("ChangeHistoryService"):SetEnabled(false)
	
		-- establish this peer as the Server --
	
		-- setup base services --
		if baseurl~=nil then
			-- players service --
			pcall(function() game:GetService("Players"):SetAbuseReportUrl(api .. "/moderation/AbuseReport/InGameChatHandler") end) --TODO: Implement
			pcall(function() game:GetService("Players"):SetChatFilterUrl(baseurl .. "/Game/ChatFilter.ashx") end) --not even used, just enables filter (lol)
			pcall(function() game:GetService("Players"):SetLoadDataUrl(baseurl .. "/Persistence/GetBlobUrl.ashx?placeId=" .. placeId .. "&userId=%d") end)
			pcall(function() game:GetService("Players"):SetSaveDataUrl(baseurl .. "/Persistence/SetBlob.ashx?placeId=" .. placeId .. "&userId=%d") end)
	
			-- scriptinformationprovider service --
			pcall(function() game:GetService("ScriptInformationProvider"):SetAssetUrl(baseurl .. "/Asset/") end)
	
			-- contentprovider service --
			pcall(function() game:GetService("ContentProvider"):SetBaseUrl(baseurl .. "/") end)
	
			-- badge service --
			pcall(function() game:GetService("BadgeService"):SetPlaceId(placeId) end)
			pcall(function() game:GetService("BadgeService"):SetAwardBadgeUrl(assetgame .. "/Game/Badge/AwardBadge?UserID=%d&BadgeID=%d&PlaceID=%d") end)
			pcall(function() game:GetService("BadgeService"):SetHasBadgeUrl(assetgame .. "/Game/Badge/HasBadge?UserID=%d&BadgeID=%d") end)
			pcall(function() game:GetService("BadgeService"):SetIsBadgeDisabledUrl(assetgame .. "/Game/Badge/IsBadgeDisabled?BadgeID=%d&PlaceID=%d") end)
			pcall(function() game:GetService("BadgeService"):SetIsBadgeLegalUrl("") end)
	
			-- social service --
			pcall(function() game:GetService("SocialService"):SetFriendUrl(assetgame .. "/Game/LuaWebService/HandleSocialRequest.ashx?method=IsFriendsWith&playerid=%d&userid=%d") end)
			pcall(function() game:GetService("SocialService"):SetBestFriendUrl(assetgame .. "/Game/LuaWebService/HandleSocialRequest.ashx?method=IsBestFriendsWith&playerid=%d&userid=%d") end)
			pcall(function() game:GetService("SocialService"):SetGroupUrl(assetgame .. "/Game/LuaWebService/HandleSocialRequest.ashx?method=IsInGroup&playerid=%d&groupid=%d") end)
			pcall(function() game:GetService("SocialService"):SetGroupRankUrl(assetgame .. "/Game/LuaWebService/HandleSocialRequest.ashx?method=GetGroupRank&playerid=%d&groupid=%d") end)
			pcall(function() game:GetService("SocialService"):SetGroupRoleUrl(assetgame .. "/Game/LuaWebService/HandleSocialRequest.ashx?method=GetGroupRole&playerid=%d&groupid=%d") end)
	
			-- gamepass service --
			pcall(function() game:GetService("GamePassService"):SetPlayerHasPassUrl(assetgame .. "/Game/GamePass/GamePassHandler.ashx?Action=HasPass&UserID=%d&PassID=%d") end)
	
			-- friends service --
			pcall(function() game:GetService("FriendService"):SetMakeFriendUrl(assetgame .. "/Game/CreateFriend?firstUserId=%d&secondUserId=%d") end)
			pcall(function() game:GetService("FriendService"):SetBreakFriendUrl(assetgame .. "/Game/BreakFriend?firstUserId=%d&secondUserId=%d") end)
			pcall(function() game:GetService("FriendService"):SetGetFriendsUrl(assetgame .. "/Game/AreFriends?userId=%d") end)
	
			-- insert service --
			pcall(function() game:GetService("InsertService"):SetBaseSetsUrl(baseurl .. "/Game/Tools/InsertAsset.ashx?nsets=10&type=base") end)
			pcall(function() game:GetService("InsertService"):SetUserSetsUrl(baseurl .. "/Game/Tools/InsertAsset.ashx?nsets=20&type=user&userid=%d") end)
			pcall(function() game:GetService("InsertService"):SetCollectionUrl(baseurl .. "/Game/Tools/InsertAsset.ashx?sid=%d") end)
			pcall(function() game:GetService("InsertService"):SetAssetUrl(baseurl .. "/Asset/?id=%d") end)
			pcall(function() game:GetService("InsertService"):SetAssetVersionUrl(baseurl .. "/Asset/?assetversionid=%d") end)
	
			-- marketplace service --
			pcall(function() game:GetService("MarketplaceService"):SetProductInfoUrl(api .. "/marketplace/productinfo?assetId=%d") end)
			pcall(function() game:GetService("MarketplaceService"):SetDevProductInfoUrl(api .. "/marketplace/productDetails?productId=%d") end)
			pcall(function() game:GetService("MarketplaceService"):SetPlayerOwnsAssetUrl(api .. "/ownership/hasasset?userId=%d&assetId=%d") end)
		end
	
		-- Set player authentication required --
		pcall(function() game:GetService("NetworkServer"):SetIsPlayerAuthenticationRequired(true) end)
	
		-- Monitor players joining --
		game:GetService("Players").PlayerAdded:connect(function(player)
			print("Player " .. player.userId .. " added")
			reportPlayerEvent(player.UserId, "Join")
			local didTeleportIn = "False"
			if player.TeleportedIn then didTeleportIn = "True" end
	
			if #game:GetService("Players"):GetPlayers() == 1 then -- so the server renews if the server has been inactive until this person joined
				sendIsAliveSignal()
			end
		end)
	
		-- Monitor players leaving --
		game:GetService("Players").PlayerRemoving:connect(function(player)
			print("Player " .. player.UserId .. " leaving")	
			reportPlayerEvent(player.UserId, "Leave")
			local isTeleportingOut = "False"
			if player.Teleported then isTeleportingOut = "True" end
	
		end)
	
		if placeId~=nil and baseurl~=nil then
			-- yield so that file load happens in the heartbeat thread
			wait()
	
			-- start isalive ping
			spawn(isAlive)
	
			-- load the game
			game:Load(baseurl .. "/asset/?id=" .. placeId)
		end
	
		-- Now start the connection
		ns:Start(port) 
	
		-- Enable scripts --
		scriptContext:SetTimeout(10)
		scriptContext.ScriptsDisabled = false
	
		------------------------------END START GAME SHARED SCRIPT--------------------------
	
		-- StartGame --
		warn('RunService:Run()')
	
		game:GetService("RunService"):Run()
	
	end