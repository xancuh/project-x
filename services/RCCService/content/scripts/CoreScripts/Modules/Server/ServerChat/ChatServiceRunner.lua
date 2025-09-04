--	// FileName: ChatServiceRunner.lua
--	// Written by: Xsitsu
--	// Description: Main script to initialize ChatService and run ChatModules.

local EventFolderName = "DefaultChatSystemChatEvents"
local EventFolderParent = game:GetService("ReplicatedStorage")
local modulesFolder = script

local PlayersService = game:GetService("Players")
local RunService = game:GetService("RunService")
local Chat = game:GetService("Chat")

local ChatService = require(modulesFolder:WaitForChild("ChatService"))

local useEvents = {}

local EventFolder = EventFolderParent:FindFirstChild(EventFolderName)
if (not EventFolder) then
	EventFolder = Instance.new("Folder")
	EventFolder.Name = EventFolderName
	EventFolder.Archivable = false
	EventFolder.Parent = EventFolderParent
end

local function GetObjectWithNameAndType(parentObject, objectName, objectType)
	for i, child in pairs(parentObject:GetChildren()) do
		if (child:IsA(objectType) and child.Name == objectName) then
			return child
		end
	end

	return nil
end

local function CreateIfDoesntExist(parentObject, objectName, objectType)
	local obj = GetObjectWithNameAndType(parentObject, objectName, objectType)
	if (not obj) then
		obj = Instance.new(objectType)
		obj.Name = objectName
		obj.Parent = parentObject
	end
	useEvents[objectName] = obj

	return obj
end

CreateIfDoesntExist(EventFolder, "OnNewMessage", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "OnMessageDoneFiltering", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "OnNewSystemMessage", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "OnChannelJoined", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "OnChannelLeft", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "OnMuted", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "OnUnmuted", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "OnMainChannelSet", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "ChannelNameColorUpdated", "RemoteEvent")

CreateIfDoesntExist(EventFolder, "SayMessageRequest", "RemoteEvent")
CreateIfDoesntExist(EventFolder, "GetInitDataRequest", "RemoteFunction")
CreateIfDoesntExist(EventFolder, "MutePlayerRequest", "RemoteFunction")
CreateIfDoesntExist(EventFolder, "UnMutePlayerRequest", "RemoteFunction")
CreateIfDoesntExist(EventFolder, "SetBlockedUserIdsRequest", "RemoteEvent")

EventFolder = useEvents


local function CreatePlayerSpeakerObject(playerObj)
	--// If a developer already created a speaker object with the
	--// name of a player and then a player joins and tries to
	--// take that name, we first need to remove the old speaker object
	local speaker = ChatService:GetSpeaker(playerObj.Name)
	if (speaker) then
		ChatService:RemoveSpeaker(playerObj.Name)
	end

	speaker = ChatService:InternalAddSpeakerWithPlayerObject(playerObj.Name, playerObj, false)

	for i, channel in pairs(ChatService:GetAutoJoinChannelList()) do
		speaker:JoinChannel(channel.Name)
	end

	speaker.ReceivedUnfilteredMessage:connect(function(messageObj, channel)
		EventFolder.OnNewMessage:FireClient(playerObj, messageObj, channel)
	end)

	speaker.MessageDoneFiltering:connect(function(messageObj, channel)
		EventFolder.OnMessageDoneFiltering:FireClient(playerObj, messageObj, channel)
	end)

	speaker.ReceivedSystemMessage:connect(function(messageObj, channel)
		EventFolder.OnNewSystemMessage:FireClient(playerObj, messageObj, channel)
	end)

	speaker.ChannelJoined:connect(function(channel, welcomeMessage)
		local log = nil
		local channelNameColor = nil

		local channelObject = ChatService:GetChannel(channel)
		if (channelObject) then
			log = channelObject:GetHistoryLogForSpeaker(speaker)
			channelNameColor = channelObject.ChannelNameColor
		end
		EventFolder.OnChannelJoined:FireClient(playerObj, channel, welcomeMessage, log, channelNameColor)
	end)

	speaker.ChannelLeft:connect(function(channel)
		EventFolder.OnChannelLeft:FireClient(playerObj, channel)
	end)

	speaker.Muted:connect(function(channel, reason, length)
		EventFolder.OnMuted:FireClient(playerObj, channel, reason, length)
	end)

	speaker.Unmuted:connect(function(channel)
		EventFolder.OnUnmuted:FireClient(playerObj, channel)
	end)

	speaker.MainChannelSet:connect(function(channel)
		EventFolder.OnMainChannelSet:FireClient(playerObj, channel)
	end)

	speaker.ChannelNameColorUpdated:connect(function(channelName, channelNameColor)
		EventFolder.ChannelNameColorUpdated:FireClient(playerObj, channelName, channelNameColor)
	end)

	ChatService:InternalFireSpeakerAdded(speaker.Name)
end

EventFolder.SayMessageRequest.OnServerEvent:connect(function(playerObj, message, channel)
	local speaker = ChatService:GetSpeaker(playerObj.Name)
	if (speaker) then
		return speaker:SayMessage(message, channel)
	end

	return nil
end)

EventFolder.MutePlayerRequest.OnServerInvoke = function(playerObj, muteSpeakerName)
	local speaker = ChatService:GetSpeaker(playerObj.Name)
	if speaker then
		local muteSpeaker = ChatService:GetSpeaker(muteSpeakerName)
		if muteSpeaker then
			speaker:AddMutedSpeaker(muteSpeaker.Name)
			return true
		end
	end
	return false
end

EventFolder.UnMutePlayerRequest.OnServerInvoke = function(playerObj, unmuteSpeakerName)
	local speaker = ChatService:GetSpeaker(playerObj.Name)
	if speaker then
		local unmuteSpeaker = ChatService:GetSpeaker(unmuteSpeakerName)
		if unmuteSpeaker then
			speaker:RemoveMutedSpeaker(unmuteSpeaker.Name)
			return true
		end
	end
	return false
end

-- Map storing Player -> Blocked user Ids.
local BlockedUserIdsMap = {}

PlayersService.PlayerAdded:connect(function(newPlayer)
	for player, blockedUsers in pairs(BlockedUserIdsMap) do
		local speaker = ChatService:GetSpeaker(player.Name)
		if speaker then
			for i = 1, #blockedUsers do
				local blockedUserId = blockedUsers[i]
				if blockedUserId == newPlayer.UserId then
					speaker:AddMutedSpeaker(newPlayer.Name)
				end
			end
		end
	end
end)

PlayersService.PlayerRemoving:connect(function(removingPlayer)
	BlockedUserIdsMap[removingPlayer] = nil
end)

EventFolder.SetBlockedUserIdsRequest.OnServerEvent:connect(function(player, blockedUserIdsList)
	BlockedUserIdsMap[player] = blockedUserIdsList
	local speaker = ChatService:GetSpeaker(player.Name)
	if speaker then
		for i = 1, #blockedUserIdsList do
			local blockedPlayer = PlayersService:GetPlayerByUserId(blockedUserIdsList[i])
			if blockedPlayer then
				speaker:AddMutedSpeaker(blockedPlayer.Name)
			end
		end
	end
end)

EventFolder.GetInitDataRequest.OnServerInvoke = (function(playerObj)
	local speaker = ChatService:GetSpeaker(playerObj.Name)
	if not (speaker and speaker:GetPlayer()) then
		CreatePlayerSpeakerObject(playerObj)
		speaker = ChatService:GetSpeaker(playerObj.Name)
	end

	local data = {}
	data.Channels = {}
	data.SpeakerExtraData = {}

	for i, channelName in pairs(speaker:GetChannelList()) do
		local channelObj = ChatService:GetChannel(channelName)
		if (channelObj) then
			local channelData =
			{
				channelName,
				channelObj:GetWelcomeMessageForSpeaker(speaker),
				channelObj:GetHistoryLogForSpeaker(speaker),
				channelObj.ChannelNameColor,
			}

			table.insert(data.Channels, channelData)
		end
	end

	for i, oSpeakerName in pairs(ChatService:GetSpeakerList()) do
		local oSpeaker = ChatService:GetSpeaker(oSpeakerName)
		data.SpeakerExtraData[oSpeakerName] = oSpeaker.ExtraData
	end

	return data
end)

local function DoJoinCommand(speakerName, channelName, fromChannelName)
	local speaker = ChatService:GetSpeaker(speakerName)
	local channel = ChatService:GetChannel(channelName)

	if (speaker) then
		if (channel) then
			if (channel.Joinable) then
				if (not speaker:IsInChannel(channel.Name)) then
					speaker:JoinChannel(channel.Name)
				else
					speaker:SetMainChannel(channel.Name)
					speaker:SendSystemMessage(string.format("You are now chatting in channel: '%s'", channel.Name), channel.Name)
				end
			else
				speaker:SendSystemMessage("You cannot join channel '" .. channelName .. "'.", fromChannelName)
			end
		else
			speaker:SendSystemMessage("Channel '" .. channelName .. "' does not exist.", fromChannelName)
		end
	end
end

local function DoLeaveCommand(speakerName, channelName, fromChannelName)
	local speaker = ChatService:GetSpeaker(speakerName)
	local channel = ChatService:GetChannel(channelName)

	if (speaker) then
		if (speaker:IsInChannel(channelName)) then
			if (channel.Leavable) then
				speaker:LeaveChannel(channel.Name)
				speaker:SendSystemMessage(string.format("You have left channel '%s'", channel.Name), "System")
			else
				speaker:SendSystemMessage("You cannot leave channel '" .. channelName .. "'.", fromChannelName)
			end
		else
			speaker:SendSystemMessage("You are not in channel '" .. channelName .. "'.", fromChannelName)
		end
	end
end

ChatService:RegisterProcessCommandsFunction("default_commands", function(fromSpeaker, message, channel)
	if (string.sub(message, 1, 6):lower() == "/join ") then
		DoJoinCommand(fromSpeaker, string.sub(message, 7), channel)
		return true
	elseif (string.sub(message, 1, 3):lower() == "/j ") then
		DoJoinCommand(fromSpeaker, string.sub(message, 4), channel)
		return true

	elseif (string.sub(message, 1, 7):lower() == "/leave ") then
		DoLeaveCommand(fromSpeaker, string.sub(message, 8), channel)
		return true
	elseif (string.sub(message, 1, 3):lower() == "/l ") then
		DoLeaveCommand(fromSpeaker, string.sub(message, 4), channel)
		return true

	elseif (string.sub(message, 1, 3) == "/e " or string.sub(message, 1, 7) == "/emote ") then
		-- Just don't show these in the chatlog. The animation script listens on these.
		return true

	end

	return false
end)


local allChannel = ChatService:AddChannel("All")
local systemChannel = ChatService:AddChannel("System")

allChannel.Leavable = false
allChannel.AutoJoin = true

allChannel:RegisterGetWelcomeMessageFunction(function(speaker)
	if RunService:IsStudio() then
		return nil
	end
	local player = speaker:GetPlayer()
	if player then
		local success, canChat = pcall(function()
			return Chat:CanUserChatAsync(player.UserId)
		end)
		if success and not canChat then
			return ""
		end
	end
end)

systemChannel.Leavable = false
systemChannel.AutoJoin = true
systemChannel.WelcomeMessage = "This channel is for system and game notifications."

systemChannel.SpeakerJoined:connect(function(speakerName)
	systemChannel:MuteSpeaker(speakerName)
end)


local function TryRunModule(module)
	if module:IsA("ModuleScript") then
		local ret = require(module)
		if (type(ret) == "function") then
			ret(ChatService)
		end
	end
end

local modules = game:GetService("Chat"):WaitForChild("ChatModules")
modules.ChildAdded:connect(function(child)
	local success, returnval = pcall(TryRunModule, child)
	if not success and returnval then
		print("Error running module " ..child.Name.. ": " ..returnval)
	end
end)

for i, module in pairs(modules:GetChildren()) do
	local success, returnval = pcall(TryRunModule, module)
	if not success and returnval then
		print("Error running module " ..module.Name.. ": " ..returnval)
	end
end

local Players = game:GetService("Players")
Players.PlayerRemoving:connect(function(playerObj)
	if (ChatService:GetSpeaker(playerObj.Name)) then
		ChatService:RemoveSpeaker(playerObj.Name)
	end
end)
