--[[
		Filename: NotificationScript2.lua
		Version 1.1
		Written by: jmargh
		Description: Handles notification gui for the following in game ROBLOX events
			Badge Awarded
			Player Points Awarded
			Friend Request Recieved/New Friend
			Graphics Quality Changed
			Teleports
			CreatePlaceInPlayerInventoryAsync
--]]

--[[ Services ]]--
local BadgeService = game:GetService('BadgeService')
local GuiService = game:GetService('GuiService')
local Players = game:GetService('Players')
local PointsService = game:GetService('PointsService')
local MarketplaceService = game:GetService('MarketplaceService')
local TeleportService = game:GetService('TeleportService')
local HttpService = game:GetService("HttpService")
local UserInputService = game:GetService("UserInputService")
local ContextActionService = game:GetService("ContextActionService")
local StarterGui = game:GetService("StarterGui")
local CoreGui = game:GetService("CoreGui")
local RobloxGui = CoreGui:WaitForChild("RobloxGui")
local Settings = UserSettings()
local GameSettings = Settings.GameSettings

--[[ Fast Flags ]]--
local getNotificationDisableSuccess, notificationsDisableActiveValue = pcall(function() return settings():GetFFlag("SetCoreDisableNotifications") end)
local allowDisableNotifications = getNotificationDisableSuccess and notificationsDisableActiveValue

local getSendNotificationSuccess, sendNotificationActiveValue = pcall(function() return settings():GetFFlag("SetCoreSendNotifications") end)
local allowSendNotifications = getSendNotificationSuccess and sendNotificationActiveValue

local getNewNotificationPathSuccess, newNotificationPathValue = pcall(function() return settings():GetFFlag("UseNewNotificationPathLua") end)
local newNotificationPath = getNewNotificationPathSuccess and newNotificationPathValue

local getTenFootBadgeNotifications, tenFootBadgeNotificationsValue = pcall(function() return settings():GetFFlag("TenFootBadgeNotifications") end)
local tenFootBadgeNotifications = getTenFootBadgeNotifications and tenFootBadgeNotificationsValue

--[[ Script Variables ]]--
local LocalPlayer = nil
while not Players.LocalPlayer do
	wait()
end
LocalPlayer = Players.LocalPlayer
local RbxGui = script.Parent
local NotificationQueue = {}
local OverflowQueue = {}
local FriendRequestBlacklist = {}
local BadgeBlacklist = {}
local CurrentGraphicsQualityLevel = GameSettings.SavedQualityLevel.Value
local BindableEvent_SendNotification = Instance.new('BindableEvent')
BindableEvent_SendNotification.Name = "SendNotification"
BindableEvent_SendNotification.Parent = RobloxGui
local BindableEvent_SendNotificationInfo = Instance.new('BindableEvent')
BindableEvent_SendNotificationInfo.Name = "SendNotificationInfo"
BindableEvent_SendNotificationInfo.Parent = RobloxGui
local isPaused = false
RobloxGui:WaitForChild("Modules"):WaitForChild("TenFootInterface")
local isTenFootInterface = require(RobloxGui.Modules.TenFootInterface):IsEnabled()

local pointsNotificationsActive = true
local badgesNotificationsActive = true

--[[ Constants ]]--
local BG_TRANSPARENCY = 0.7
local MAX_NOTIFICATIONS = 3

local NOTIFICATION_Y_OFFSET = isTenFootInterface and 145 or 64
local NOTIFICATION_TITLE_Y_OFFSET = isTenFootInterface and 40 or 12
local NOTIFICATION_TEXT_Y_OFFSET = isTenFootInterface and -16 or 1
local NOTIFICATION_FRAME_WIDTH = isTenFootInterface and 450 or 200
local NOTIFICATION_TEXT_HEIGHT = isTenFootInterface and 85 or 28
local NOTIFICATION_TITLE_FONT_SIZE = isTenFootInterface and Enum.FontSize.Size42 or Enum.FontSize.Size18
local NOTIFICATION_TEXT_FONT_SIZE = isTenFootInterface and Enum.FontSize.Size36 or Enum.FontSize.Size14

local IMAGE_SIZE = isTenFootInterface and 72 or 48

local EASE_DIR = Enum.EasingDirection.InOut
local EASE_STYLE = Enum.EasingStyle.Sine
local TWEEN_TIME = 0.35
local DEFAULT_NOTIFICATION_DURATION = 5
local FRIEND_REQUEST_NOTIFICATION_THROTTLE = 5

local friendRequestNotificationFIntSuccess, friendRequestNotificationFIntValue = pcall(function() return tonumber(settings():GetFVariable("FriendRequestNotificationThrottle")) end)
if friendRequestNotificationFIntSuccess and friendRequestNotificationFIntValue ~= nil then
	FRIEND_REQUEST_NOTIFICATION_THROTTLE = friendRequestNotificationFIntValue
end

--[[ Images ]]--
local PLAYER_POINTS_IMG = 'https://www.roblox.com/asset?id=206410433'
local BADGE_IMG = 'https://www.roblox.com/asset?id=206410289'
local FRIEND_IMAGE = 'https://www.roblox.com/thumbs/avatar.ashx?userId='

--[[ Gui Creation ]]--
local function createFrame(name, size, position, bgt)
	local frame = Instance.new('Frame')
	frame.Name = name
	frame.Size = size
	frame.Position = position
	frame.BackgroundTransparency = bgt

	return frame
end

local function createTextButton(name, text, position)
	local button = Instance.new('TextButton')
	button.Name = name
	button.Size = UDim2.new(0.5, -2, 0.5, 0)
	button.Position = position
	button.BackgroundTransparency = BG_TRANSPARENCY
	button.BackgroundColor3 = Color3.new(0, 0, 0)
	button.Font = Enum.Font.SourceSansBold
	button.FontSize = Enum.FontSize.Size18
	button.TextColor3 = Color3.new(1, 1, 1)
	button.Text = text

	return button
end

local NotificationFrame = createFrame("NotificationFrame", UDim2.new(0, NOTIFICATION_FRAME_WIDTH, 0.42, 0), UDim2.new(1, -NOTIFICATION_FRAME_WIDTH-4, 0.50, 0), 1.0)
NotificationFrame.Parent = RbxGui

local DefaultNotification = createFrame("Notification", UDim2.new(1, 0, 0, NOTIFICATION_Y_OFFSET), UDim2.new(0, 0, 0, 0), BG_TRANSPARENCY)
DefaultNotification.BackgroundColor3 = Color3.new(0, 0, 0)
DefaultNotification.BorderSizePixel = 0

local NotificationTitle = Instance.new('TextLabel')
NotificationTitle.Name = "NotificationTitle"
NotificationTitle.Size = UDim2.new(0, 0, 0, 0)
NotificationTitle.Position = UDim2.new(0.5, 0, 0.5, -12)
NotificationTitle.BackgroundTransparency = 1
NotificationTitle.Font = Enum.Font.SourceSansBold
NotificationTitle.FontSize = NOTIFICATION_TITLE_FONT_SIZE
NotificationTitle.TextColor3 = Color3.new(0.97, 0.97, 0.97)

local NotificationText = Instance.new('TextLabel')
NotificationText.Name = "NotificationText"
NotificationText.Size = UDim2.new(1, -20, 0, 28)
NotificationText.Position = UDim2.new(0, 10, 0.5, 1)
NotificationText.BackgroundTransparency = 1
NotificationText.Font = Enum.Font.SourceSans
NotificationText.FontSize = NOTIFICATION_TEXT_FONT_SIZE
NotificationText.TextColor3 = Color3.new(0.92, 0.92, 0.92)
NotificationText.TextWrap = true
NotificationText.TextYAlignment = Enum.TextYAlignment.Top

local NotificationImage = Instance.new('ImageLabel')
NotificationImage.Name = "NotificationImage"
NotificationImage.Size = UDim2.new(0, IMAGE_SIZE, 0, IMAGE_SIZE)
NotificationImage.Position = UDim2.new(0, (1.0/6.0) * IMAGE_SIZE, 0, 0.5 * (NOTIFICATION_Y_OFFSET - IMAGE_SIZE))
NotificationImage.BackgroundTransparency = 1
NotificationImage.Image = ""

-- Would really like to get rid of this but some events still require this
local PopupFrame = createFrame("PopupFrame", UDim2.new(0, 360, 0, 160), UDim2.new(0.5, -180, 0.5, -50), 0)
PopupFrame.Style = Enum.FrameStyle.DropShadow
PopupFrame.ZIndex = 4
PopupFrame.Visible = false
PopupFrame.Parent = RbxGui

local PopupAcceptButton = Instance.new('TextButton')
PopupAcceptButton.Name = "PopupAcceptButton"
PopupAcceptButton.Size = UDim2.new(0, 100, 0, 50)
PopupAcceptButton.Position = UDim2.new(0.5, -102, 1, -58)
PopupAcceptButton.Style = Enum.ButtonStyle.RobloxRoundButton
PopupAcceptButton.Font = Enum.Font.SourceSansBold
PopupAcceptButton.FontSize = Enum.FontSize.Size24
PopupAcceptButton.TextColor3 = Color3.new(1, 1, 1)
PopupAcceptButton.Text = "Accept"
PopupAcceptButton.ZIndex = 5
PopupAcceptButton.Parent = PopupFrame

local PopupDeclineButton = PopupAcceptButton:Clone()
PopupDeclineButton.Name = "PopupDeclineButton"
PopupDeclineButton.Position = UDim2.new(0.5, 2, 1, -58)
PopupDeclineButton.Text = "Decline"
PopupDeclineButton.Parent = PopupFrame

local PopupOKButton = PopupAcceptButton:Clone()
PopupOKButton.Name = "PopupOKButton"
PopupOKButton.Position = UDim2.new(0.5, -50, 1, -58)
PopupOKButton.Text = "OK"
PopupOKButton.Visible = false
PopupOKButton.Parent = PopupFrame

local PopupText = Instance.new('TextLabel')
PopupText.Name = "PopupText"
PopupText.Size = UDim2.new(1, -16, 0.8, 0)
PopupText.Position = UDim2.new(0, 8, 0, 8)
PopupText.BackgroundTransparency = 1
PopupText.Font = Enum.Font.SourceSansBold
PopupText.FontSize = Enum.FontSize.Size36
PopupText.TextColor3 = Color3.new(0.97, 0.97, 0.97)
PopupText.TextWrap = true
PopupText.ZIndex = 5
PopupText.TextYAlignment = Enum.TextYAlignment.Top
PopupText.Text = "This is a popup"
PopupText.Parent = PopupFrame

--[[ Helper Functions ]]--
local insertNotification = nil
local removeNotification = nil

local function getFriendImage(playerId, size)
	size = size or 48
	return ("http://www.roblox.com/thumbs/avatar.ashx?userId=%d&x=%d&y=%d"):format(playerId, size, size)
end

--
local function createNotification(title, text, image)
	local notificationFrame = DefaultNotification:Clone()
	notificationFrame.Position = UDim2.new(1, 4, 1, -NOTIFICATION_Y_OFFSET - 4)
	--
	local notificationTitle = NotificationTitle:Clone()
	notificationTitle.Text = title
	notificationTitle.Parent = notificationFrame

	local notificationText = NotificationText:Clone()
	notificationText.Text = text
	notificationText.Parent = notificationFrame

	if image and image ~= "" then
		local notificationImage = NotificationImage:Clone()
		notificationImage.Image = image
		notificationImage.Parent = notificationFrame

		notificationTitle.Position = UDim2.new(0, (4.0/3.0) * IMAGE_SIZE, 0.5, -NOTIFICATION_TITLE_Y_OFFSET)
		notificationTitle.TextXAlignment = Enum.TextXAlignment.Left

		notificationText.Size = UDim2.new(1, -IMAGE_SIZE - 16, 0, NOTIFICATION_TEXT_HEIGHT)
		notificationText.Position = UDim2.new(0, (4.0/3.0) * IMAGE_SIZE, 0.5, NOTIFICATION_TEXT_Y_OFFSET)
		notificationText.TextXAlignment = Enum.TextXAlignment.Left
	end

	GuiService:AddSelectionParent(HttpService:GenerateGUID(false), notificationFrame)

	return notificationFrame
end

local function findNotification(notification)
	local index = nil
	for i = 1, #NotificationQueue do
		if NotificationQueue[i] == notification then
			return i
		end
	end
end

local function updateNotifications()
	local pos = 1
	local yOffset = 0
	for i = #NotificationQueue, 1, -1 do
		local currentNotification = NotificationQueue[i]
		if currentNotification then
			local frame = currentNotification.Frame
			if frame and frame.Parent then
				local thisOffset = currentNotification.IsFriend and (NOTIFICATION_Y_OFFSET + 2) * 1.5 or NOTIFICATION_Y_OFFSET
				yOffset = yOffset + thisOffset
				frame:TweenPosition(UDim2.new(0, 0, 1, -yOffset - (pos * 4)), EASE_DIR, EASE_STYLE, TWEEN_TIME, true,
					function()
						if currentNotification.TweenOutCallback then
							currentNotification.TweenOutCallback()
						end
					end)
				pos = pos + 1
			end
		end
	end
end

local lastTimeInserted = 0
insertNotification = function(notification)
	spawn(function()
		while isPaused do wait() end
		notification.IsActive = true
		local size = #NotificationQueue
		if size == MAX_NOTIFICATIONS then
			NotificationQueue[1].Duration = 0
			OverflowQueue[#OverflowQueue + 1] = notification
			return
		end

		NotificationQueue[size + 1] = notification
		notification.Frame.Parent = NotificationFrame

		spawn(function()
			wait(TWEEN_TIME * 2)
			-- Wait for it to tween in, and then wait that same amount of time before calculating lifetime.
			-- This is to have it not zoom out while half tweened in when the OverflowQueue forcibly
			-- makes room for itself.

			while(notification.Duration > 0) do
				wait(0.2)
				notification.Duration = notification.Duration - 0.2
			end

			removeNotification(notification)
		end)
--[[
		delay(notification.Duration, function()
			removeNotification(notification)
		end)
]]
		while tick() - lastTimeInserted < TWEEN_TIME do
			wait()
		end
		lastTimeInserted = tick()
		--
		updateNotifications()
	end)
end

removeNotification = function(notification)
	if not notification then return end
	--
	local index = findNotification(notification)
	table.remove(NotificationQueue, index)
	local frame = notification.Frame
	if frame and frame.Parent then
		notification.IsActive = false
		spawn(function()
			while isPaused do wait() end

			-- Tween out now, or set up to tween out immediately after current tween is finished, but don't interrupt.
			local function doTweenOut()
				return frame:TweenPosition(UDim2.new(1, 0, 1, frame.Position.Y.Offset), EASE_DIR, EASE_STYLE, TWEEN_TIME, false,
				function()
					frame:Destroy()
					notification = nil
				end)
			end

			if (not doTweenOut()) then
				notification.TweenOutCallback = doTweenOut
			end

		end)
	end
	if #OverflowQueue > 0 then
		local nextNotification = OverflowQueue[1]
		table.remove(OverflowQueue, 1)

		insertNotification(nextNotification)

		if (#OverflowQueue > 0) then
			NotificationQueue[1].Duration = 0
		end
	else
		updateNotifications()
	end
end

local function sendNotificationInfo(notificationInfo)
	notificationInfo.Duration = notificationInfo.Duration or DEFAULT_NOTIFICATION_DURATION
	BindableEvent_SendNotificationInfo:Fire(notificationInfo)
end
local function sendNotification(title, text, image, duration, callback, button1Text, button2Text, groupName)
	--TODO: remove this flag check when stable
	if newNotificationPath then
		--translate to the new method of sending a notification
		local notificationInfo = {
			GroupName = groupName,
			Title = title,
			Text = text,
			Image = image,
			Duration = duration,
			Callback = callback,
			Button1Text = button1Text,
			Button2Text = button2Text
		}
		sendNotificationInfo(notificationInfo)
	else
		BindableEvent_SendNotification:Fire(title, text, image, duration, callback, button1Text, button2Text, groupName)
	end


end
local function onSendNotification(title, text, image, duration, callback, button1Text, button2Text)
	if UserInputService.VREnabled then
		--If VR is enabled, notifications will be handled by Modules.VR.NotificationHub
		return
	end

	local notification = {}
	local notificationFrame = createNotification(title, text, image)
	--

	local button1 = nil
	if button1Text and button1Text ~= "" then
		notification.IsFriend = true -- Prevents other notifications overlapping the buttons
		button1 = createTextButton("Button1", button1Text, UDim2.new(0, 0, 1, 2))
		button1.Parent = notificationFrame
		local button1ClickedConnection = nil
		button1ClickedConnection = button1.MouseButton1Click:connect(function()
			if button1ClickedConnection then
				button1ClickedConnection:disconnect()
				button1ClickedConnection = nil
				removeNotification(notification)
				if callback and type(callback) ~= "function" then -- callback should be a bindable
					pcall(function() callback:Invoke(button1Text) end)
				elseif type(callback) == "function" then
					callback(button1Text)
				end
			end
		end)
	end

	if button2Text and button2Text ~= "" then
		notification.IsFriend = true
		local button2 = createTextButton("Button1", button2Text, UDim2.new(0.5, 2, 1, 2))
		button2.Parent = notificationFrame
		local button2ClickedConnection = nil
		button2ClickedConnection = button2.MouseButton1Click:connect(function()
			if button2ClickedConnection then
				button2ClickedConnection:disconnect()
				button2ClickedConnection = nil
				removeNotification(notification)
				if callback and type(callback) ~= "function" then -- callback should be a bindable
					pcall(function() callback:Invoke(button2Text) end)
				elseif type(callback) == "function" then
					callback(button2Text)
				end
			end
		end)
	else
		-- Resize button1 to take up all the space under the notification if button2 doesn't exist
		if button1 then
			button1.Size = UDim2.new(1, -2, .5, 0)
		end
	end

	notification.Frame = notificationFrame
	notification.Duration = duration
	insertNotification(notification)
end
BindableEvent_SendNotification.Event:connect(onSendNotification)

local function onSendNotificationInfo(notificationInfo)
	if UserInputService.VREnabled then
		--If VR is enabled, notifications will be handled by Modules.VR.NotificationHub
		return
	end

	--If we hit this, it means that the newNotificationPath flag is on and still exists.
	--When the flag is removed, we should also merge its contents into this one.
	--Right now, calling sendNotification(...) will essentially translate into a sendNotificationInfo(table), which will then be translated back
	--into a parameter list here. One option in the future is to update the code that produces the notification in non-VR to use notificationInfo table.
	onSendNotification(notificationInfo.Title, notificationInfo.Text, notificationInfo.Image, notificationInfo.Duration, notificationInfo.Callback, notificationInfo.Button1Text, notificationInfo.Button2Text)
end
BindableEvent_SendNotificationInfo.Event:connect(onSendNotificationInfo)

-- New follower notification
spawn(function()
	local RobloxReplicatedStorage = game:GetService('RobloxReplicatedStorage')
	local RemoteEvent_NewFollower = RobloxReplicatedStorage:WaitForChild('NewFollower', 86400) or RobloxReplicatedStorage:WaitForChild('NewFollower')
	--
	RemoteEvent_NewFollower.OnClientEvent:connect(function(followerRbxPlayer)
		if newNotificationPath then
			local message = ("%s is now following you"):format(followerRbxPlayer.Name)
			local image = getFriendImage(followerRbxPlayer.UserId)
			sendNotificationInfo {
				GroupName = "Friends",
				Title = "New Follower",
				Text = message,
				DetailText = message,
				Image = image,
				Duration = 5
			}
		else
			sendNotification("New Follower", followerRbxPlayer.Name.." is now following you!",
				FRIEND_IMAGE..followerRbxPlayer.UserId.."&x=48&y=48", 5, function() end)
		end
	end)
end)

local checkFriendRequestIsThrottled; do
	local friendRequestThrottlingMap = {}
	
	checkFriendRequestIsThrottled = function(fromPlayer)
		local throttleFinishedTime = friendRequestThrottlingMap[fromPlayer]
		
		if throttleFinishedTime then
			if tick() < throttleFinishedTime then
				return true
			end
		end

		friendRequestThrottlingMap[fromPlayer] = tick() + FRIEND_REQUEST_NOTIFICATION_THROTTLE
		return false
	end
end

local function sendFriendNotification(fromPlayer)
	--TODO: remove this flag check when stable
	if newNotificationPath then
		if checkFriendRequestIsThrottled(fromPlayer) then
			return
		end
	
		local acceptText = "Accept"
		local declineText = "Decline"
		sendNotificationInfo {
			GroupName = "Friends",
			Title = fromPlayer.Name,
			Text = "Sent you a friend request!",
			DetailText = fromPlayer.Name,
			Image = getFriendImage(fromPlayer.UserId),
			Duration = 8,
			Callback = function(buttonChosen)
				if buttonChosen == acceptText then
					LocalPlayer:RequestFriendship(fromPlayer)
				else
					LocalPlayer:RevokeFriendship(fromPlayer)
					FriendRequestBlacklist[fromPlayer] = true
				end
			end,
			Button1Text = acceptText,
			Button2Text = declineText
		}
	else
		local notification = {}
		local notificationFrame = createNotification(fromPlayer.Name, "Sent you a friend request!",
			FRIEND_IMAGE..tostring(fromPlayer.UserId).."&x=48&y=48")
		notificationFrame.Position = UDim2.new(1, 4, 1, -(NOTIFICATION_Y_OFFSET + 2) * 1.5 - 4)
		--
		local acceptButton = createTextButton("AcceptButton", "Accept", UDim2.new(0, 0, 1, 2))
		acceptButton.Parent = notificationFrame

		local declineButton = createTextButton("DeclineButton", "Decline", UDim2.new(0.5, 2, 1, 2))
		declineButton.Parent = notificationFrame

		acceptButton.MouseButton1Click:connect(function()
			if not notification.IsActive then return end
			if notification then
				removeNotification(notification)
			end
			LocalPlayer:RequestFriendship(fromPlayer)
		end)

		declineButton.MouseButton1Click:connect(function()
			if not notification.IsActive then return end
			if notification then
				removeNotification(notification)
			end
			LocalPlayer:RevokeFriendship(fromPlayer)
			FriendRequestBlacklist[fromPlayer] = true
		end)

		notification.Frame = notificationFrame
		notification.Duration = 8
		notification.IsFriend = true
		insertNotification(notification)
	end
end

--[[ Friend Notifications ]]--
local function onFriendRequestEvent(fromPlayer, toPlayer, event)
	if fromPlayer ~= LocalPlayer and toPlayer ~= LocalPlayer then return end
	--
	if fromPlayer == LocalPlayer then
		if event == Enum.FriendRequestEvent.Accept then
			if newNotificationPath then
				sendNotificationInfo {
					GroupName = "Friends",
					Title = "New Friend",
					Text = toPlayer.Name,
					DetailText = "You are now friends with " .. toPlayer.Name .. "!",


					Image = getFriendImage(toPlayer.UserId),
					Duration = DEFAULT_NOTIFICATION_DURATION
				}
			else
				sendNotification("New Friend", "You are now friends with "..toPlayer.Name.."!",
					FRIEND_IMAGE..tostring(toPlayer.UserId).."&x=48&y=48", DEFAULT_NOTIFICATION_DURATION, nil, nil, nil, "Friends")
			end
		end
	elseif toPlayer == LocalPlayer then
		if event == Enum.FriendRequestEvent.Issue then
			if FriendRequestBlacklist[fromPlayer] then return end
			sendFriendNotification(fromPlayer)
		elseif event == Enum.FriendRequestEvent.Accept then
			if newNotificationPath then
				sendNotificationInfo {
					GroupName = "Friends",
					Title = "New Friend",
					Text = fromPlayer.Name,
					DetailText = "You are now friends with " .. fromPlayer.Name .. "!",

					Image = getFriendImage(fromPlayer.UserId),
					Duration = DEFAULT_NOTIFICATION_DURATION
				}
			else
				sendNotification("New Friend", "You are now friends with "..fromPlayer.Name.."!",
					FRIEND_IMAGE..tostring(fromPlayer.UserId).."&x=48&y=48", DEFAULT_NOTIFICATION_DURATION, nil, nil, nil, "Friends")
			end
		end
	end
end

--[[ Player Points Notifications ]]--
local function onPointsAwarded(userId, pointsAwarded, userBalanceInGame, userTotalBalance)
	if newNotificationPath then
		if pointsNotificationsActive and userId == LocalPlayer.UserId then
			local title, text, detailText
			if pointsAwarded == 1 then
				title = "Point Awarded"
				text = "You received 1 point!"
			elseif pointsAwarded > 0 then
				title = "Points Awarded"
				text = ("You received %d points!"):format(pointsAwarded)
			elseif pointsAwarded < 0 then
				title = "Points Lost"
				text = ("You lost %d points!"):format(math.abs(pointsAwarded))
			else
				--don't notify for 0 points, shouldn't even happen
				return
			end
			detailText = text

			sendNotificationInfo {
				GroupName = "PlayerPoints",
				Title = title,
				Text = text,
				DetailText = detailText,
				Image = PLAYER_POINTS_IMG,
				Duration = DEFAULT_NOTIFICATION_DURATION
			}
		end
	else
		if pointsNotificationsActive and userId == LocalPlayer.UserId then
			if pointsAwarded == 1 then
				sendNotification("Point Awarded", "You received "..tostring(pointsAwarded).." point!", PLAYER_POINTS_IMG, DEFAULT_NOTIFICATION_DURATION, nil, nil, nil, "PlayerPoints")
			elseif pointsAwarded > 0 then
				sendNotification("Points Awarded", "You received "..tostring(pointsAwarded).." points!", PLAYER_POINTS_IMG, DEFAULT_NOTIFICATION_DURATION, nil, nil, nil, "PlayerPoints")
			elseif pointsAwarded < 0 then
				sendNotification("Points Lost", "You lost "..tostring(-pointsAwarded).." points!", PLAYER_POINTS_IMG, DEFAULT_NOTIFICATION_DURATION, nil, nil, nil, "PlayerPoints")
			end
		end
	end
end

--[[ Badge Notification ]]--
local function onBadgeAwarded(message, userId, badgeId)
	if not BadgeBlacklist[badgeId] and badgesNotificationsActive and userId == LocalPlayer.UserId then
		BadgeBlacklist[badgeId] = true
		if newNotificationPath then
			sendNotificationInfo {
				GroupName = "BadgeAwards",
				Title = "Badge Awarded",
				Text = message,
				DetailText = message,
				Image = BADGE_IMG,
				Duration = DEFAULT_NOTIFICATION_DURATION
			}
		else
			sendNotification("Badge Awarded", message, BADGE_IMG, DEFAULT_NOTIFICATION_DURATION, nil, nil, nil, "BadgeAwards")
		end
	end
end

--[[ Graphics Changes Notification ]]--
function onGameSettingsChanged(property, amount)
	if property == "SavedQualityLevel" then
		local level = GameSettings.SavedQualityLevel.Value + amount
		if level > 10 then
			level = 10
		elseif level < 1 then
			level = 1
		end
		-- value of 0 is automatic, we do not want to send a notification in that case
		if level > 0 and level ~= CurrentGraphicsQualityLevel and GameSettings.SavedQualityLevel ~= Enum.SavedQualitySetting.Automatic then
			local action = (level > CurrentGraphicsQualityLevel) and "Increased" or "Decreased"
			local message = ("%s to (%d)"):format(action, level)
			if newNotificationPath then
				sendNotificationInfo {
					GroupName = "Graphics",
					Title = "Graphics Quality",
					Text = message,
					DetailText = message,
					Image = "",
					Duration = 2
				}
			else
				sendNotification("Graphics Quality", message, "", 2, nil)
			end
			CurrentGraphicsQualityLevel = level
		end
	end
end

--[[ Connections ]]--

if tenFootBadgeNotifications then

BadgeService.BadgeAwarded:connect(onBadgeAwarded)
if not isTenFootInterface then
	Players.FriendRequestEvent:connect(onFriendRequestEvent)
	PointsService.PointsAwarded:connect(onPointsAwarded)
	--GameSettings.Changed:connect(onGameSettingsChanged)
	game.GraphicsQualityChangeRequest:connect(function(graphicsIncrease) --graphicsIncrease is a boolean
		onGameSettingsChanged("SavedQualityLevel", graphicsIncrease == true and 1 or -1)
	end)
end

else

if not isTenFootInterface then
	Players.FriendRequestEvent:connect(onFriendRequestEvent)
	PointsService.PointsAwarded:connect(onPointsAwarded)
	BadgeService.BadgeAwarded:connect(onBadgeAwarded)
	--GameSettings.Changed:connect(onGameSettingsChanged)
	game.GraphicsQualityChangeRequest:connect(function(graphicsIncrease) --graphicsIncrease is a boolean
		onGameSettingsChanged("SavedQualityLevel", graphicsIncrease == true and 1 or -1)
	end)
end

end

GuiService.SendCoreUiNotification = function(title, text)
	local notification = createNotification(title, text, "")
	notification.BackgroundTransparency = .5
	notification.Size = UDim2.new(.5, 0, .1, 0)
	notification.Position = UDim2.new(.25, 0, -0.1, 0)
	notification.NotificationTitle.FontSize = Enum.FontSize.Size36
	notification.NotificationText.FontSize = Enum.FontSize.Size24
	notification.Parent = RbxGui
	notification:TweenPosition(UDim2.new(.25, 0, 0, 0), EASE_DIR, EASE_STYLE, TWEEN_TIME, true)
	wait(5)
	if notification then
		notification:Destroy()
	end
end

--[[ Market Place Events ]]--
-- This is used for when a player calls CreatePlaceInPlayerInventoryAsync
local function onClientLuaDialogRequested(msg, accept, decline)
	PopupText.Text = msg
	--
	local acceptCn, declineCn = nil, nil
	local function disconnectCns()
		if acceptCn then acceptCn:disconnect() end
		if declineCn then declineCn:disconnect() end
		--
		GuiService:RemoveCenterDialog(PopupFrame)
		PopupFrame.Visible = false
	end

	acceptCn = PopupAcceptButton.MouseButton1Click:connect(function()
		disconnectCns()
		MarketplaceService:SignalServerLuaDialogClosed(true)
	end)
	declineCn = PopupDeclineButton.MouseButton1Click:connect(function()
		disconnectCns()
		MarketplaceService:SignalServerLuaDialogClosed(false)
	end)

	local centerDialogSuccess = pcall(
		function()
			GuiService:AddCenterDialog(PopupFrame, Enum.CenterDialogType.QuitDialog,
				function()
					PopupOKButton.Visible = false
					PopupAcceptButton.Visible = true
					PopupDeclineButton.Visible = true
					PopupAcceptButton.Text = accept
					PopupDeclineButton.Text = decline
					PopupFrame.Visible = true
				end,
				function()
					PopupFrame.Visible = false
				end)
		end)

	if not centerDialogSuccess then
		PopupFrame.Visible = true
		PopupAcceptButton.Text = accept
		PopupDeclineButton.Text = decline
	end

	return true
end
MarketplaceService.ClientLuaDialogRequested:connect(onClientLuaDialogRequested)

--[[ Developer customization API ]]--
local function createDeveloperNotification(notificationTable)
	if type(notificationTable) == "table" then
		if type(notificationTable.Title) == "string" and type(notificationTable.Text) == "string" then
			local iconImage = (type(notificationTable.Icon) == "string" and notificationTable.Icon or "")
			local duration = (type(notificationTable.Duration) == "number" and notificationTable.Duration or DEFAULT_NOTIFICATION_DURATION)
			local bindable = (typeof(notificationTable.Callback) == "Instance" and notificationTable.Callback:IsA("BindableFunction") and notificationTable.Callback or nil)
			local button1Text = (type(notificationTable.Button1) == "string" and notificationTable.Button1 or "")
			local button2Text = (type(notificationTable.Button2) == "string" and notificationTable.Button2 or "")
			if newNotificationPath then
				sendNotificationInfo {
					GroupName = "Developer",
					Title = notificationTable.Title,
					Text = notificationTable.Text,
					Image = iconImage,
					Duration = duration,
					Callback = bindable,
					Button1Text = button1Text,
					Button2Text = button2Text
				}
			else
				sendNotification(notificationTable.Title, notificationTable.Text, iconImage, duration, bindable, button1Text, button2Text, "Developer")
			end
		end
	end
end

if allowDisableNotifications then
	StarterGui:RegisterSetCore("PointsNotificationsActive", function(value) if type(value) == "boolean" then pointsNotificationsActive = value end end)
	StarterGui:RegisterSetCore("BadgesNotificationsActive", function(value) if type(value) == "boolean" then badgesNotificationsActive = value end end)
else
	StarterGui:RegisterSetCore("PointsNotificationsActive", function() end)
	StarterGui:RegisterSetCore("BadgesNotificationsActive", function() end)
end

StarterGui:RegisterGetCore("PointsNotificationsActive", function() return pointsNotificationsActive end)
StarterGui:RegisterGetCore("BadgesNotificationsActive", function() return badgesNotificationsActive end)

if allowSendNotifications then
	StarterGui:RegisterSetCore("SendNotification", createDeveloperNotification)
else
	StarterGui:RegisterSetCore("SendNotification", function() end)
end


if not isTenFootInterface then
	local gamepadMenu = RobloxGui:WaitForChild("CoreScripts/GamepadMenu")
	local gamepadNotifications = gamepadMenu:FindFirstChild("GamepadNotifications")
	while not gamepadNotifications do
		wait()
		gamepadNotifications = gamepadMenu:FindFirstChild("GamepadNotifications")
	end

	local leaveNotificationFunc = function(name, state, inputObject)
		if state ~= Enum.UserInputState.Begin then return end

		if GuiService.SelectedCoreObject:IsDescendantOf(NotificationFrame) then
			GuiService.SelectedCoreObject = nil
		end

		ContextActionService:UnbindCoreAction("LeaveNotificationSelection")
	end

	gamepadNotifications.Event:connect(function(isSelected)
		if not isSelected then return end

		isPaused = true
		local notifications = NotificationFrame:GetChildren()
		for i = 1, #notifications do
			local noteComponents = notifications[i]:GetChildren()
			for j = 1, #noteComponents do
				if noteComponents[j]:IsA("GuiButton") and noteComponents[j].Visible then
					GuiService.SelectedCoreObject = noteComponents[j]
					break
				end
			end
		end

		if GuiService.SelectedCoreObject then
			ContextActionService:BindCoreAction("LeaveNotificationSelection", leaveNotificationFunc, false, Enum.KeyCode.ButtonB)
		else
			isPaused = false
			local utility = require(RobloxGui.Modules.Settings.Utility)
			local okPressedFunc = function() end
			utility:ShowAlert("You have no notifications", "Ok", settingsHub, okPressedFunc, true)
		end
	end)

	GuiService.Changed:connect(function(prop)
		if prop == "SelectedCoreObject" then
			if not GuiService.SelectedCoreObject or not GuiService.SelectedCoreObject:IsDescendantOf(NotificationFrame) then
				isPaused = false
			end
		end
	end)
end

local UserInputService = game:GetService('UserInputService')
local Platform = UserInputService:GetPlatform()
local Modules = RobloxGui:FindFirstChild('Modules')
local CSMModule = Modules:FindFirstChild('ControllerStateManager')
if Modules and not CSMModule then
	local ShellModules = Modules:FindFirstChild('Shell')
	if ShellModules then
		CSMModule = ShellModules:FindFirstChild('ControllerStateManager')
	end
end

if Platform == Enum.Platform.XBoxOne then
	-- Platform hook for controller connection events
	-- Displays overlay to user on controller connection lost
	local PlatformService = nil
	pcall(function() PlatformService = game:GetService('PlatformService') end)
	if PlatformService and CSMModule then
		local controllerStateManager = require(CSMModule)
		if controllerStateManager then
			controllerStateManager:Initialize()

			if not game:IsLoaded() then
				game.Loaded:wait()
			end

			-- retro check in case of controller disconnect while loading
			-- for now, gamepad1 is always mapped to the active user
			controllerStateManager:CheckUserConnected()
		end
	end
end

