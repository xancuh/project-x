@echo STARTING RCC
start cmd /k cd "game-server" ^& call run.bat
start cmd /k cd "RCCService" ^& call run.bat
timeout /t 4 /nobreak
@echo STARTING SITE
start cmd /k cd "2016-roblox-main" ^& call run.bat
sstart cmd /k cd "game-server" ^& call run.bat
sstart cmd /k cd "RCCService" ^& call run.bat
start cmd /k cd "AssetValidationServiceV2" ^& call run.bat
start cmd /k cd "Roblox/Roblox.Website" ^& call run.bat