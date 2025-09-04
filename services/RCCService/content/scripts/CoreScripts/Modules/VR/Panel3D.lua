--Panel3D: 3D GUI panels for VR
--written by 0xBAADF00D
--revised/refactored 5/11/16

local UserInputService = game:GetService("UserInputService")
local VRServiceExists, VRService = pcall(function() return game:GetService("VRService") end)
local RunService = game:GetService("RunService")
local GuiService = game:GetService("GuiService")
local CoreGui = game:GetService("CoreGui")
local RobloxGui = CoreGui:WaitForChild("RobloxGui")
local PlayersService = game:GetService("Players")
local Utility = require(RobloxGui.Modules.Settings.Utility)

--Panel3D State variables
local renderStepName = "Panel3DRenderStep-" .. game:GetService("HttpService"):GenerateGUID()
local defaultPixelsPerStud = 64
local pointUpCF = CFrame.Angles(math.rad(-90), math.rad(180), 0)
local zeroVector = Vector3.new(0, 0, 0)
local zeroVector2 = Vector2.new(0, 0)
local turnAroundCF = CFrame.Angles(0, math.rad(180), 0)
local fullyOpaqueAtPixelsFromEdge = 10
local fullyTransparentAtPixelsFromEdge = 80
local partThickness = 0.2

local cursorHidden = false
local cursorHideTime = 2.5

local currentModal = nil
local lastModal = nil
local currentMaxDist = math.huge
local currentClosest = nil
local currentCursorParent = nil
local currentCursorPos = zeroVector2
local lastClosest = nil
local currentHeadScale = 1
local panels = {}
local floorRotation = CFrame.new()
local cursor = Utility:Create "ImageLabel" {
	Image = "rbxasset://textures/Cursors/Gamepad/Pointer.png",
	Size = UDim2.new(0, 8, 0, 8),
	BackgroundTransparency = 1,
	ZIndex = 10
}
local partFolder = Utility:Create "Folder" {
	Name = "VRCorePanelParts",
	Archivable = false
}
local effectFolder = Utility:Create "Folder" {
	Name = "VRCoreEffectParts",
	Archivable = false
}
pcall(function()
	GuiService.CoreGuiFolder = partFolder
	GuiService.CoreEffectFolder = effectFolder
end)
--End of Panel3D State variables


--Panel3D Declaration and enumerations
local Panel3D = {}
Panel3D.Type = {
	None = 0,
--	Floor = 1, todo: remove when deemed safe
	Fixed = 2,
	HorizontalFollow = 3,
	FixedToHead = 4
}

Panel3D.OnPanelClosed = Utility:Create 'BindableEvent' {
	Name = 'OnPanelClosed'
}

function Panel3D.GetHeadLookXZ(withTranslation)
	local userHeadCF = UserInputService:GetUserCFrame(Enum.UserCFrame.Head)
	local headLook = userHeadCF.lookVector
	local headYaw = math.atan2(-headLook.Z, headLook.X) + math.rad(90)
	local cf = CFrame.Angles(0, headYaw, 0)

	if withTranslation then
		cf = cf + userHeadCF.p
	end
	return cf
end

function Panel3D.FindContainerOf(element)
	for _, panel in pairs(panels) do
		if panel.gui and panel.gui:IsAncestorOf(element) then
			return panel
		end
		for _, subpanel in pairs(panel.subpanels) do
			if subpanel.gui and subpanel.gui:IsAncestorOf(element) then
				return panel
			end
		end
	end
	return nil
end

function Panel3D.SetModalPanel(panel)
	if currentModal == panel then
		return
	end
	if currentModal then
		currentModal:OnModalChanged(false)
	end
	if panel then
		panel:OnModalChanged(true)
	end
	lastModal = currentModal
	currentModal = panel
end

function Panel3D.RaycastOntoPanel(part, parentGui, gui, ray)
	local partSize = part.Size
	local partThickness = partSize.Z
	local partWidth = partSize.X
	local partHeight = partSize.Y

	local planeCF = part:GetRenderCFrame()
	local planeNormal = planeCF.lookVector
	local pointOnPlane = planeCF.p + (planeNormal * partThickness * 0.5)

	--Find where the view ray intersects with the plane in world space
	local worldIntersectPoint = Utility:RayPlaneIntersection(ray, planeNormal, pointOnPlane)
	if worldIntersectPoint then
		local parentGuiWidth, parentGuiHeight = parentGui.AbsoluteSize.X, parentGui.AbsoluteSize.Y
		--now figure out where that intersection point was in the panel's local space
		--and then flip the X axis because the plane is looking back at you (panel's local +X is to the left of the camera)
		--and then offset it by half of the panel's size in X and -Y to move 0,0 to the upper-left of the panel.
		local localIntersectPoint = planeCF:pointToObjectSpace(worldIntersectPoint) * Vector3.new(-1, 1, 1) + Vector3.new(partWidth / 2, -partHeight / 2, 0)
		--now scale it into the gui space on the panel's surface
		local lookAtPixel = Vector2.new((localIntersectPoint.X / partWidth) * parentGuiWidth, (localIntersectPoint.Y / partHeight) * -parentGuiHeight)
		
		--fire mouse enter/leave events if necessary
		local lookX, lookY = lookAtPixel.X, lookAtPixel.Y
		local guiX, guiY = gui.AbsolutePosition.X, gui.AbsolutePosition.Y
		local guiWidth, guiHeight = gui.AbsoluteSize.X, gui.AbsoluteSize.Y
		local isOnGui = false

		if parentGui.Enabled then
			if lookX >= guiX and lookX <= guiX + guiWidth and
			   lookY >= guiY and lookY <= guiY + guiHeight then
			   	isOnGui = true
			end
		end

		return worldIntersectPoint, localIntersectPoint, lookAtPixel, isOnGui
	else
		return nil, nil, nil, false
	end
end

--End of Panel3D Declaration and enumerations


--Cursor autohiding methods
local cursorHidden = false
local hasTool = false
local lastMouseActivity = tick()
local lastMouseBehavior = Enum.MouseBehavior.Default

local function OnCharacterAdded(character)
	hasTool = false
	for i, v in ipairs(character:GetChildren()) do
		if v:IsA("Tool") then
			hasTool = true
		end
	end
	character.ChildAdded:connect(function(child)
		if child:IsA("Tool") then
			hasTool = true
			lastMouseActivity = tick() --kick the mouse when a tool is equipped
		end
	end)
	character.ChildRemoved:connect(function(child)
		if child:IsA("Tool") then
			hasTool = false
		end
	end)
end
spawn(function()
	while not PlayersService.LocalPlayer do wait() end
	PlayersService.LocalPlayer.CharacterAdded:connect(OnCharacterAdded)
	if PlayersService.LocalPlayer.Character then OnCharacterAdded(PlayersService.LocalPlayer.Character) end
end)
local function autoHideCursor(hide)
	if not PlayersService.LocalPlayer then
		cursorHidden = false
		return
	end
	if not UserInputService.VREnabled then
		cursorHidden = false
		return
	end
	if hide then
		--don't hide if there's a tool in the character
		local character = PlayersService.LocalPlayer.Character
		if character and hasTool then
			return
		end
		cursorHidden = true
		UserInputService.OverrideMouseIconBehavior = Enum.OverrideMouseIconBehavior.ForceHide
	else
		cursorHidden = false
		UserInputService.OverrideMouseIconBehavior = Enum.OverrideMouseIconBehavior.None
	end
end

local function isCursorVisible()
	--if ForceShow, the cursor is definitely visible at all times
	if UserInputService.OverrideMouseIconBehavior == Enum.OverrideMouseIconBehavior.ForceShow then
		return true
	end
	--if ForceHide, the cursor is definitely NOT visible
	if UserInputService.OverrideMouseIconBehavior == Enum.OverrideMouseIconBehavior.ForceHide then
		return false
	end
	--Otherwise, we need to check if the developer set MouseIconEnabled=false
	if UserInputService.MouseIconEnabled and UserInputService.OverrideMouseIconBehavior == Enum.OverrideMouseIconBehavior.None then
		return true
	end
	return false
end

--End of cursor autohiding methods


--Panel class implementation
local Panel = {}
Panel.__index = Panel
function Panel.new(name)
	local self = {}
	self.name = name

	self.part = false
	self.gui = false

	self.width = 1
	self.height = 1

	self.isVisible = false
	self.isEnabled = false
	self.panelType = Panel3D.Type.None
	self.pixelScale = 1
	self.showCursor = true
	self.canFade = true
	self.shouldFindLookAtGuiElement = false
	self.ignoreModal = false

	self.linkedTo = false
	self.subpanels = {}

	self.transparency = 1
	self.forceShowUntilLookedAt = false
	self.isLookedAt = false
	self.isOffscreen = true
	self.lookAtPixel = Vector2.new(-1, -1)
	self.cursorPos = Vector2.new(-1, -1)
	self.lookAtDistance = math.huge
	self.lookAtGuiElement = false
	self.isClosest = true

	self.localCF = CFrame.new()
	self.angleFromHorizon = false
	self.angleFromForward = false
	self.distance = false

	if panels[name] then
		error("A panel by the name of " .. name .. " already exists.")
	end
	panels[name] = self

	return setmetatable(self, Panel)
end

--Panel accessor methods
function Panel:GetPart()
	if not self.part then
		self.part = Utility:Create "Part" {
			Name = self.name,
			Parent = partFolder,

			Transparency = 1,

			CanCollide = false,
			Anchored = true,

			Size = Vector3.new(1, 1, partThickness)
		}
	end
	return self.part
end

function Panel:GetGUI()
	if not self.gui then
		local part = self:GetPart()
		self.gui = Utility:Create "SurfaceGui" {
			Parent = CoreGui,
			Name = self.name,
			Archivable = false,
			Adornee = part,
			Active = true,
			ToolPunchThroughDistance = 1000,
			CanvasSize = self.CanvasSize or Vector2.new(0, 0),
			Enabled = self.isEnabled,
			AlwaysOnTop = true
		}
	end
	return self.gui
end

function Panel:FindHoveredGuiElement(elements)
	local x, y = self.lookAtPixel.X, self.lookAtPixel.Y
	for i, v in pairs(elements) do
		local minPt = v.AbsolutePosition
		local maxPt = v.AbsolutePosition + v.AbsoluteSize
		if minPt.X <= x and maxPt.X >= x and
		   minPt.Y <= y and maxPt.Y >= y then
			return v, i
		end
	end
end
--End of panel accessor methods


--Panel update methods
function Panel:SetPartCFrame(cframe)
	self:GetPart().CFrame = cframe * CFrame.new(0, 0, -0.5 * partThickness)
end

function Panel:SetEnabled(enabled)
	if self.isEnabled == enabled then
		return
	end

	self.isEnabled = enabled
	if enabled then
		self:GetPart().Parent = partFolder
		self:GetGUI().Enabled = true
		for i, v in pairs(self.subpanels) do
			v:SetEnabled(v:GetEnabled())
		end
	else
		self:GetPart().Parent = nil
		self:GetGUI().Enabled = false
		for i, v in pairs(self.subpanels) do
			v:SetEnabled(v:GetEnabled())
		end
	end

	self:OnEnabled(enabled)
end

function Panel:EvaluatePositioning(cameraCF, cameraRenderCF, userHeadCF)
	if self.panelType == Panel3D.Type.Fixed then
		--Places the panel in the camera's local space, but doesn't follow the user's head.
		--Useful if you know what you're doing. localCF can be updated in PreUpdate for animation.
		local cf = self.localCF - self.localCF.p
		cf = cf + (self.localCF.p * currentHeadScale)
		self:SetPartCFrame(cameraCF * cf)
	elseif self.panelType == Panel3D.Type.HorizontalFollow then
		local headLook = userHeadCF.lookVector
		local headYaw = math.atan2(-headLook.Z, headLook.X) + math.rad(90)
		local headForwardCF = CFrame.Angles(0, headYaw, 0) + userHeadCF.p
		local localCF = (headForwardCF * self.angleFromForward) * --Rotate about Y (left-right)
						self.angleFromHorizon * --Rotate about X (up-down)
						CFrame.new(0, 0, currentHeadScale * self.distance)-- * --Move into scene
						--turnAroundCF --Turn around to face character
		self:SetPartCFrame(cameraCF * localCF)
	elseif self.panelType == Panel3D.Type.FixedToHead then
		--Places the panel in the user's head local space. localCF can be updated in PreUpdate for animation.
		local cf = self.localCF - self.localCF.p
		cf = cf + (self.localCF.p * currentHeadScale)
		self:SetPartCFrame(cameraRenderCF * cf)
	end
end

function Panel:SetLookedAt(lookedAt)
	if not self.isLookedAt and lookedAt then
		self.isLookedAt = true
		self:OnMouseEnter(self.lookAtPixel.X, self.lookAtPixel.Y)
		if self.forceShowUntilLookedAt then
			self.forceShowUntilLookedAt = false
		end
	elseif self.isLookedAt and not lookedAt then
		self.isLookedAt = false
		self:OnMouseLeave(self.lookAtPixel.X, self.lookAtPixel.Y)
	end
end

function Panel:EvaluateGaze(cameraCF, cameraRenderCF, userHeadCF, lookRay, pointerRay)
	--reset distance data
	self.isClosest = false
	self.lookAtPixel = zeroVector2
	self.lookAtDistance = math.huge

	--check all subpanels first, they're usually in front of the panel.
	local highestSubpanel = nil
	local highestSubpanelDepth = 0
	for guiElement, subpanel in pairs(self.subpanels) do
		if subpanel.part and subpanel.guiElement then
			--note that we're passing subpanel.guiElement and not subpanel.gui
			--this is on purpose so we can fall through to the panels underneath since subpanels will rarely take up the whole 
			--panel size.
			local worldIntersectPoint, localIntersectPoint, guiPixelHit, isOnGui = Panel3D.RaycastOntoPanel(subpanel.part, subpanel.gui, subpanel.guiElement, pointerRay)
			if worldIntersectPoint then
				subpanel.lookAtPixel = guiPixelHit
				subpanel.cursorPos = guiPixelHit

				if isOnGui and subpanel.depthOffset > highestSubpanelDepth then
					highestSubpanel = subpanel
					highestSubpanelDepth = subpanel.depthOffset
				end
			end
		end
	end

	if highestSubpanel and highestSubpanel.depthOffset > 0 then
		currentCursorParent = highestSubpanel.gui
		currentCursorPos = highestSubpanel.cursorPos
		currentClosest = highestSubpanel

		for _, subpanel in pairs(self.subpanels) do
			if subpanel ~= highestSubpanel then
				subpanel:SetLookedAt(false)
			end
		end
		highestSubpanel:SetLookedAt(true)
	end

	local gui = self:GetGUI()
	local worldIntersectPoint, localIntersectPoint, guiPixelHit, isOnGui = Panel3D.RaycastOntoPanel(self:GetPart(), gui, gui, pointerRay)
	if worldIntersectPoint then
		self.isOffscreen = false

		--transform worldIntersectPoint to gui space
		self.lookAtPixel = guiPixelHit
		self.cursorPos = guiPixelHit

		--fire mouse enter/leave events if necessary
		self:SetLookedAt(isOnGui)

		--evaluate distance
		self.lookAtDistance = (worldIntersectPoint - cameraRenderCF.p).magnitude
		if self.isLookedAt and self.lookAtDistance < currentMaxDist and self.showCursor then
			currentMaxDist = self.lookAtDistance
			currentClosest = self
			if not highestSubpanel then
				currentCursorParent = self.gui
				currentCursorPos = self.cursorPos
			end
		end
	else
		self.isOffscreen = true

		--Not looking at the plane at all, so fire off mouseleave if necessary.
		if self.isLookedAt then
			self.isLookedAt = false
			self:OnMouseLeave(self.lookAtPixel.X, self.lookAtPixel.Y)
		end
	end
end

function Panel:EvaluateTransparency()
	--Early exit if force shown
	if self.forceShowUntilLookedAt or not self.canFade then
		self.transparency = 0
		return
	end
	--Early exit if we're looking at the panel (no transparency!)
	if self.isLookedAt then
		self.transparency = 0
		return
	end
	--Similarly, exit if we can't possibly see the panel.
	if self.isOffscreen then
		self.transparency = 1
		return
	end
	--Otherwise, we'll want to calculate the transparency.
	self.transparency = self:CalculateTransparency()
end

function Panel:Update(cameraCF, cameraRenderCF, userHeadCF, lookRay, pointerRay, dt)
	if self.forceShowUntilLookedAt and not self.part then
		self:GetPart()
		self:GetGUI()
	end
	if not self.part then
		return
	end

	local isModal = (currentModal == self)
	if not isModal and self.linkedTo and self.linkedTo == currentModal then
		isModal = true
	end
	if currentModal and not isModal then
		self:SetEnabled(false)
		return
	end

	self:PreUpdate(cameraCF, cameraRenderCF, userHeadCF, lookRay, dt)
	if self.isVisible then
		self:EvaluatePositioning(cameraCF, cameraRenderCF, userHeadCF)
		for i, v in pairs(self.subpanels) do
			v:Update()
		end
		self:EvaluateGaze(cameraCF, cameraRenderCF, userHeadCF, lookRay, pointerRay)

		self:EvaluateTransparency(cameraCF, cameraRenderCF)
	end
end
--End of Panel update methods

--Panel virtual methods
function Panel:PreUpdate(cameraCF, cameraRenderCF, userHeadCF, lookRay, dt) --virtual: handle positioning here
end

function Panel:OnUpdate(dt) --virtual: handle transparency here
end

function Panel:OnMouseEnter(x, y) --virtual
end

function Panel:OnMouseLeave(x, y) --virtual
end

function Panel:OnEnabled(enabled) --virtual
end

function Panel:OnModalChanged(isModal) --virtual
end

function Panel:OnVisibilityChanged(visible) --virtual
end

function Panel:CalculateTransparency() --virtual
	if not self.canFade then
		return 0
	end

	local guiWidth, guiHeight = self.gui.AbsoluteSize.X, self.gui.AbsoluteSize.Y
	local lookX, lookY = self.lookAtPixel.X, self.lookAtPixel.Y

	--Determine the distance from the edge; 
	--if x is negative it's on the left side, meaning the distance is just absolute value
	--if x is positive it's on the right side, meaning the distance is x minus the width
	local xEdgeDist = lookX < 0 and -lookX or (lookX - guiWidth)
	local yEdgeDist = lookY < 0 and -lookY or (lookY - guiHeight)
	if lookX > 0 and lookX < guiWidth then
		xEdgeDist = 0
	end
	if lookY > 0 and lookY < guiHeight then
		yEdgeDist = 0
	end
	local edgeDist = math.sqrt(xEdgeDist ^ 2 + yEdgeDist ^ 2)

	--since transparency is 0-1, we know how many pixels will give us 0 and how many will give us 1.
	local offset = fullyOpaqueAtPixelsFromEdge
	local interval = fullyTransparentAtPixelsFromEdge
	--then we just clamp between 0 and 1.
	return math.max(0, math.min(1, (edgeDist - offset) / interval))
end
--End of Panel virtual methods


--Panel configuration methods
function Panel:ResizeStuds(width, height, pixelsPerStud)
	pixelsPerStud = pixelsPerStud or defaultPixelsPerStud

	self.width = width
	self.height = height

	self.pixelScale = pixelsPerStud / defaultPixelsPerStud

	local part = self:GetPart()
	part.Size = Vector3.new(self.width * currentHeadScale, self.height * currentHeadScale, partThickness)
	local gui = self:GetGUI()
	gui.CanvasSize = Vector2.new(pixelsPerStud * self.width, pixelsPerStud * self.height)

	for i, v in pairs(self.subpanels) do
		if v.part then
			v.part.Size = part.Size
		end
		if v.gui then
			v.gui.CanvasSize = gui.CanvasSize
		end
	end
end

function Panel:ResizePixels(width, height, pixelsPerStud)
	pixelsPerStud = pixelsPerStud or defaultPixelsPerStud

	local widthInStuds = width / pixelsPerStud
	local heightInStuds = height / pixelsPerStud
	self:ResizeStuds(widthInStuds, heightInStuds, pixelsPerStud)
end

function Panel:OnHeadScaleChanged(newHeadScale)
	local pixelsPerStud = self.pixelScale * defaultPixelsPerStud
	self:ResizeStuds(self.width, self.height, pixelsPerStud)
end

function Panel:SetType(panelType, config)
	self.panelType = panelType

	--clear out old type-specific members

	self.localCF = CFrame.new()

	self.angleFromHorizon = false
	self.angleFromForward = false
	self.distance = false

	if not config then
		config = {}
	end

	if panelType == Panel3D.Type.None then
		--nothing to do
		return
	elseif panelType == Panel3D.Type.Floor then
		self.floorPos = config.FloorPosition or Vector3.new(0, 0, 0)
	elseif panelType == Panel3D.Type.Fixed then
		self.localCF = config.CFrame or CFrame.new()
	elseif panelType == Panel3D.Type.HorizontalFollow then
		self.angleFromHorizon = CFrame.Angles(config.angleFromHorizon or 0, 0, 0)
		self.angleFromForward = CFrame.Angles(0, config.angleFromForward or 0, 0)
		self.distance = config.distance or 5
	elseif panelType == Panel3D.Type.FixedToHead then
		self.localCF = config.CFrame or CFrame.new()
	else
		error("Invalid Panel type")
	end
end

function Panel:SetVisible(visible, modal)
	if visible ~= self.isVisible then
		self:OnVisibilityChanged(visible)
		if not visible then
			Panel3D.OnPanelClosed:Fire(self.name)
		end
	end

	self.isVisible = visible
	self:SetEnabled(visible)
	if visible and modal then
		Panel3D.SetModalPanel(self)
	end
	if not visible and currentModal == self then
		if modal and not doNotRestore then
			--restore last modal panel
			Panel3D.SetModalPanel(lastModal)
		else
			Panel3D.SetModalPanel(nil)

			--if the coder explicitly wanted to hide this modal panel,
			--it follows that they don't want it to be restored when the next
			--modal panel is hidden.
			if lastModal == self then
				lastModal = nil
			end
		end
	end

	if not visible and self.forceShowUntilLookedAt then
		self.forceShowUntilLookedAt = false
	end
end

function Panel:IsVisible()
	return self.isVisible
end

function Panel:LinkTo(panelName)
	if type(panelName) == "string" then
		self.linkedTo = Panel3D.Get(panelName)
	else
		self.linkedTo = panelName
	end
end

function Panel:ForceShowUntilLookedAt(makeModal)
	--ensure the part exists
	self:GetPart()
	self:GetGUI()

	self:SetVisible(true, makeModal)
	self.forceShowUntilLookedAt = true
end

function Panel:SetCanFade(canFade)
	self.canFade = canFade
end

--Child class, Subpanel
local Subpanel = {}
Subpanel.__index = Subpanel
function Subpanel.new(parentPanel, guiElement)
	local self = {}
	self.parentPanel = parentPanel
	self.guiElement = guiElement
	self.lastParent = guiElement.Parent
	self.ancestryConn = nil
	self.changedConn = nil

	self.lookAtPixel = Vector2.new(-1, -1)
	self.cursorPos = Vector2.new(-1, -1)
	self.lookedAt = false

	self.isEnabled = true

	self.part = nil
	self.gui = nil
	self.guiSurrogate = nil

	self.depthOffset = 0

	setmetatable(self, Subpanel)


	self:GetGUI()
	self:UpdateSurrogate()
	self:WatchParent(self.lastParent)

	guiElement.Parent = self.guiSurrogate
	
	local function ancestryCallback(parent, child)
		self:GetGUI().Enabled = self.parentPanel:GetGUI():IsAncestorOf(self.lastParent)
		if not self:GetGUI().Enabled then
			self:GetPart().Parent = nil
		else
			self:GetPart().Parent = workspace.CurrentCamera
		end
		if child == guiElement then
			--disconnect the event because we're going to move this element
			self.ancestryConn:disconnect()

			self.lastParent = guiElement.Parent
			guiElement.Parent = self.guiSurrogate
			self:WatchParent(self.lastParent)

			--reconnect it
			self.ancestryConn = guiElement.AncestryChanged:connect(ancestryCallback)
		end
	end
	self.ancestryConn = guiElement.AncestryChanged:connect(ancestryCallback)

	return self
end

function Subpanel:Cleanup()
	self.guiElement.Parent = self.lastParent
	if self.part then
		self.part:Destroy()
		self.part = nil
	end
	spawn(function()
		wait() --wait so anything that's in the gui that doesn't want to be has time to get out (panel cursor for example)
		if self.gui then
			self.gui:Destroy()
			self.gui = nil
		end
	end)
	if self.ancestryConn then
		self.ancestryConn:disconnect()
		self.ancestryConn = nil
	end
	if self.changedConn then
		self.changedConn:disconnect()
		self.changedConn = nil
	end
	self.lastParent = nil
	self.parentPanel = nil
	self.guiElement = nil
	self.guiSurrogate = nil
end

function Subpanel:OnMouseEnter(x, y)
end
function Subpanel:OnMouseLeave(x, y)
end

function Subpanel:SetLookedAt(lookedAt)
	if lookedAt and not self.lookedAt then
		self:OnMouseEnter(self.lookAtPixel.X, self.lookAtPixel.Y)
	elseif not lookedAt and self.lookedAt then
		self:OnMouseLeave(self.lookAtPixel.X, self.lookAtPixel.Y)
	end
	self.lookedAt = lookedAt
end

function Subpanel:WatchParent(parent)
	if self.changedConn then
		self.changedConn:disconnect()
	end
	self.changedConn = parent.Changed:connect(function(prop)
		if prop == "AbsolutePosition" or prop == "AbsoluteSize" or prop == "Parent" then
			self:UpdateSurrogate()
		end
	end)
end

function Subpanel:UpdateSurrogate()
	local lastParent = self.lastParent
	self.guiSurrogate.Position = UDim2.new(0, lastParent.AbsolutePosition.X, 0, lastParent.AbsolutePosition.Y)
	self.guiSurrogate.Size = UDim2.new(0, lastParent.AbsoluteSize.X, 0, lastParent.AbsoluteSize.Y)
end

function Subpanel:GetPart()
	if self.part then
		return self.part
	end

	self.part = self.parentPanel:GetPart():Clone()
	self.part.Parent = partFolder
	return self.part
end

function Subpanel:GetGUI()
	if self.gui then
		return self.gui
	end

	self.gui = Utility:Create "SurfaceGui" {
		Parent = CoreGui,
		Adornee = self:GetPart(),
		Active = true,
		ToolPunchThroughDistance = 1000,
		CanvasSize = self.parentPanel:GetGUI().CanvasSize,
		Enabled = self.parentPanel.isEnabled,
		AlwaysOnTop = false
	}
	self.guiSurrogate = Utility:Create "Frame" {
		Parent = self.gui,

		Active = false,

		Position = UDim2.new(0, 0, 0, 0),
		Size = UDim2.new(1, 0, 1, 0),

		BackgroundTransparency = 1
	}
	return self.gui
end

function Subpanel:SetDepthOffset(offset)
	self.depthOffset = offset
end

function Subpanel:Update()
	local part = self:GetPart()
	local parentPart = self.parentPanel:GetPart()

	if part and parentPart then
		part.CFrame = parentPart.CFrame * CFrame.new(0, 0, -self.depthOffset)
	end
end

function Subpanel:SetEnabled(enabled)
	-- Don't change check here, parentPanel may try to refresh our enabled state
	-- alternatively we could listen to an enabled changed event on our parent panel
	self.isEnabled = enabled
	if enabled and self.parentPanel.isEnabled then
		self:GetPart().Parent = partFolder
		self:GetGUI().Enabled = true
	else
		self:GetPart().Parent = nil
		self:GetGUI().Enabled = false
	end
end

function Subpanel:GetEnabled()
	return self.isEnabled
end

function Subpanel:GetPixelScale()
	return self.parentPanel:GetPixelScale()
end
function Panel:GetPixelScale()
	return self.pixelScale
end

function Panel:AddSubpanel(guiElement)
	local subpanel = Subpanel.new(self, guiElement)
	self.subpanels[guiElement] = subpanel
	return subpanel
end

function Panel:RemoveSubpanel(guiElement)
	local subpanel = self.subpanels[guiElement]
	if subpanel then
		subpanel:Cleanup()
	end
	self.subpanels[guiElement] = nil
end

function Panel:SetSubpanelDepth(guiElement, depth)
	local subpanel = self.subpanels[guiElement]

	if depth == 0 then
		if subpanel then
			self:RemoveSubpanel(guiElement)
		end
		return nil
	end

	if not subpanel then
		subpanel = self:AddSubpanel(guiElement)
	end
	subpanel:SetDepthOffset(depth)

	return subpanel
end

--End of Panel configuration methods
--End of Panel class implementation


--Panel3D API
function Panel3D.Get(name)
	local panel = panels[name] 
	if not panels[name] then
		panels[name] = Panel.new(name)
		panel = panels[name]
	end
	return panel
end
--End of Panel3D API


--Panel3D Setup
local frameStart = tick()
local function onRenderStep()
	if not UserInputService.VREnabled then
		return
	end

	local now = tick()
	local dt = now - frameStart
	frameStart = now
	

	--reset distance info
	currentClosest = nil
	currentMaxDist = math.huge

	--figure out some useful stuff
	local camera = workspace.CurrentCamera
	local cameraCF = camera.CFrame
	local cameraRenderCF = camera:GetRenderCFrame()
	local userHeadCF = UserInputService:GetUserCFrame(Enum.UserCFrame.Head)
	local lookRay = Ray.new(cameraRenderCF.p, cameraRenderCF.lookVector)

	local inputUserCFrame = Enum.UserCFrame.Head
	if VRServiceExists then
		inputUserCFrame = VRService.GuiInputUserCFrame
	end
	local inputCF = cameraCF * UserInputService:GetUserCFrame(inputUserCFrame)
	local pointerRay = Ray.new(inputCF.p, inputCF.lookVector)

	--allow all panels to run their own update code
	for i, v in pairs(panels) do
		v:Update(cameraCF, cameraRenderCF, userHeadCF, lookRay, pointerRay, dt)
	end

	--evaluate linked panels
	local processed = {}
	for i, v in pairs(panels) do
		if not processed[v] and v.linkedTo and v.isVisible and v.linkedTo.isVisible then
			processed[v] = true
			processed[v.linkedTo] = true

			local minTransparency = math.min(v.transparency, v.linkedTo.transparency)
			v.transparency = minTransparency
			v.linkedTo.transparency = minTransparency
		end
	end

	--run post update because the distance information hasn't been
	--finalized until now.
	for i, v in pairs(panels) do
		--If the part is fully transparent, we don't want to keep it around in the workspace.
		if v.part and v.gui then
			--check if this panel is the current modal panel
			local isModal = (currentModal == v)
			--but also check if this panel is linked to the current modal panel
			if not isModal and v.linkedTo and v.linkedTo == currentModal then
				isModal = true
			end

			local show = v.isVisible
			if not isModal and currentModal then
				show = false
			end
			if v.transparency >= 1 then
				show = false
			end

			if v.forceShowUntilLookedAt then
				show = true
			end
			if not v.canFade and v.isVisible then
				show = true
			end
			
			v:SetEnabled(show)
		end

		v:OnUpdate(dt)
	end

	--place the cursor on the closest panel (for now)
	if not currentClosest and lastClosest then
		UserInputService.MouseBehavior = lastMouseBehavior
	elseif currentClosest and not lastClosest then
		lastMouseBehavior = UserInputService.MouseBehavior
	end

	if currentClosest then
		UserInputService.MouseBehavior = Enum.MouseBehavior.LockCenter
		UserInputService.OverrideMouseIconBehavior = Enum.OverrideMouseIconBehavior.ForceHide
		cursor.Parent = currentCursorParent

		local x, y = currentCursorPos.X, currentCursorPos.Y
		local pixelScale = currentClosest:GetPixelScale()
		cursor.Size = UDim2.new(0, 8 * pixelScale, 0, 8 * pixelScale)
		cursor.Position = UDim2.new(0, x - cursor.AbsoluteSize.x * 0.5, 0, y - cursor.AbsoluteSize.y * 0.5)
	else
		cursor.Parent = nil
	end
	lastClosest = currentClosest
end

--Implement cursor autohide functionality
UserInputService.InputChanged:connect(function(inputObj, processed)
	if inputObj.UserInputType == Enum.UserInputType.MouseMovement then
		lastMouseActivity = tick()
		autoHideCursor(false)
	end
end)
local function onHeartbeat()
	if isCursorVisible() then
		cursorHidden = false
	end
	if lastMouseActivity + cursorHideTime < tick() and not GuiService.MenuIsOpen and not cursorHidden then
		autoHideCursor(true)
	end
end
RunService.Heartbeat:connect(onHeartbeat)



local cameraChangedConnection = nil
local function onCameraChanged(prop)
	if prop == "HeadScale" then
		pcall(function()
			currentHeadScale = workspace.CurrentCamera.HeadScale
		end)
		for i, v in pairs(panels) do
			v:OnHeadScaleChanged(currentHeadScale)
		end
	end
end

local function onWorkspaceChanged(prop)
	if prop == "CurrentCamera" then
		onCameraChanged("HeadScale")
		if cameraChangedConnection then
			cameraChangedConnection:disconnect()
		end
		cameraChangedConnection = workspace.CurrentCamera.Changed:connect(onCameraChanged)

		if UserInputService.VREnabled then
			partFolder.Parent = workspace.CurrentCamera
			effectFolder.Parent = workspace.CurrentCamera
		end
	end
end

local currentCameraChangedConn = nil
local renderStepFuncBound = false
local function onVREnabled(prop)
	if prop == "VREnabled" then
		if UserInputService.VREnabled then
			if workspace.CurrentCamera then
				onWorkspaceChanged("CurrentCamera")
			end
			currentCameraChangedConn = workspace.Changed:connect(onWorkspaceChanged)

			partFolder.Parent = workspace.CurrentCamera
			effectFolder.Parent = workspace.CurrentCamera
			
			if not renderStepFuncBound then
				RunService:BindToRenderStep(renderStepName, Enum.RenderPriority.Last.Value, onRenderStep)
				renderStepFuncBound = true
			end
		else
			if currentCameraChangedConn then
				currentCameraChangedConn:disconnect()
				currentCameraChangedConn = nil
			end
			partFolder.Parent = nil
			effectFolder.Parent = nil
			
			if renderStepFuncBound then
				RunService:UnbindFromRenderStep(renderStepName)
				renderStepFuncBound = false
			end
		end
	end
end
UserInputService.Changed:connect(onVREnabled)
onVREnabled("VREnabled")

return Panel3D