--	// FileName: ChatConstants.lua
--	// Written by: TheGamer101
--	// Description: Module for creating chat constants shared between server and client.

local module = {}

---[[ Message Types ]]
module.MessageTypeDefault = "Message"
module.MessageTypeSystem = "System"
module.MessageTypeMeCommand = "MeCommand"
module.MessageTypeWelcome = "Welcome"
module.MessageTypeSetCore = "SetCore"
module.MessageTypeWhisper = "Whisper"

--[[ Version ]]
module.MajorVersion = 0
module.MinorVersion = 5

---[[ Command/Filter Priorities ]]
module.VeryLowPriority = -5
module.LowPriority = 0
module.StandardPriority = 10
module.HighPriority = 20
module.VeryHighPriority = 25

return module
