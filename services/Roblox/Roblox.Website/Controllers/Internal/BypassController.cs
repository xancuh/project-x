using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Web;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Net.Http.Headers;
using Roblox.Dto.Persistence;
using Roblox.Dto.Users;
using MVC = Microsoft.AspNetCore.Mvc;
using Roblox.Libraries.Assets;
using Roblox.Libraries.FastFlag;
using Roblox.Libraries.RobloxApi;
using Roblox.Logging;
using Roblox.Services.Exceptions;
using BadRequestException = Roblox.Exceptions.BadRequestException;
using Roblox.Models.Assets;
using Roblox.Models.GameServer;
using Roblox.Models.Users;
using Roblox.Services;
using Roblox.Services.App.FeatureFlags;
using Roblox.Website.Filters;
using Roblox.Website.Middleware;
using Roblox.Website.WebsiteModels.Asset;
using Roblox.Website.WebsiteModels.Games;
using HttpGet = Roblox.Website.Controllers.HttpGetBypassAttribute;
using MultiGetEntry = Roblox.Dto.Assets.MultiGetEntry;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;
using ServiceProvider = Roblox.Services.ServiceProvider;
using Type = Roblox.Models.Assets.Type;
using Microsoft.EntityFrameworkCore;
using Dapper;

namespace Roblox.Website.Controllers
{
    [MVC.ApiController]
    [MVC.Route("/")]
    public class BypassController : ControllerBase
    {


        [HttpGet("internal/release-metadata")]
        public dynamic GetReleaseMetaData([Required] string requester)
        {
            throw new RobloxException(RobloxException.BadRequest, 0, "BadRequest");
        }

        [HttpGet("asset/shader")]
        public async Task<MVC.FileResult> GetShaderAsset(long id)
        {
            var isMaterialOrShader = BypassControllerMetadata.materialAndShaderAssetIds.Contains(id);
            if (!isMaterialOrShader)
            {
                // Would redirect but that could lead to infinite loop.
                // Just throw instead
                throw new RobloxException(400, 0, "BadRequest");
            }

            var assetId = id;
            try
            {
                var ourId = await services.assets.GetAssetIdFromRobloxAssetId(assetId);
                assetId = ourId;
            }
            catch (RecordNotFoundException)
            {
                // Doesn't exist yet, so create it
                var migrationResult = await MigrateItem.MigrateItemFromRoblox(assetId.ToString(), false, null, default, new ProductDataResponse()
                {
                    Name = "ShaderConversion" + id,
                    AssetTypeId = Type.Special, // Image
                    Created = DateTime.UtcNow,
                    Updated = DateTime.UtcNow,
                    Description = "ShaderConversion1.0",
                });
                assetId = migrationResult.assetId;
            }

            var latestVersion = await services.assets.GetLatestAssetVersion(assetId);
            if (latestVersion.contentUrl is null)
            {
                throw new RobloxException(403, 0, "Forbidden"); // ?
            }
            // These files are large, encourage clients to cache them
            HttpContext.Response.Headers.CacheControl = new CacheControlHeaderValue()
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(360),
            }.ToString();
            var assetContent = await services.assets.GetAssetContent(latestVersion.contentUrl);
            return File(assetContent, "application/binary");
        }

        private bool IsRcc()
        {
            var rccAccessKey = Request.Headers.ContainsKey("accessKey") ? Request.Headers["accessKey"].ToString() : null;
            var isRcc = rccAccessKey == Configuration.RccAuthorization;
            return isRcc;
        }

        [HttpGet("asset")]
        public async Task<MVC.ActionResult> GetAssetById(long id)
        {
            // TODO: This endpoint needs to be updated to return a URL to the asset, not the asset itself.
            // The reason for this is so that cloudflare can cache assets without caching the response of this endpoint, which might be different depending on the client making the request (e.g. under 18 user, over 18 user, rcc, etc).
            var is18OrOver = false;
            if (userSession != null)
            {
                is18OrOver = await services.users.Is18Plus(userSession.userId);
            }           
            

            var assetId = id;
            var invalidIdKey = "InvalidAssetIdForConversionV1:" + assetId;
            // Opt
            if (Services.Cache.distributed.StringGetMemory(invalidIdKey) != null)
                throw new RobloxException(400, 0, "Asset is invalid or does not exist");
            var cacheControl = "no-cache, no-store, must-revalidate";
            Response.Headers.Add("Cache-Control", cacheControl);
            var isBotRequest = Request.Headers["bot-auth"].ToString() == Roblox.Configuration.BotAuthorization;
            var isLoggedIn = userSession != null;
            var encryptionEnabled = !isBotRequest; // bots can't handle encryption :(
            var userAgent = Request.Headers["User-Agent"].FirstOrDefault()?.ToLower();
            var requester = Request.Headers["Requester"].FirstOrDefault()?.ToLower();
            
            
            if (!isBotRequest && !isLoggedIn) {
                if (userAgent is null) throw new BadRequestException();
                if (requester is null) throw new BadRequestException();
                // Client = studio/client, Server = rcc
                if (requester != "client" && requester != "server")
                {
                    throw new BadRequestException();
                }

                if (!BypassControllerMetadata.allowedUserAgents.Contains(userAgent))
                {
                    throw new BadRequestException();
                }
            }
            



            var isMaterialOrShader = BypassControllerMetadata.materialAndShaderAssetIds.Contains(assetId);
            if (isMaterialOrShader)
            {
                return new MVC.RedirectResult("/asset/shader?id=" + assetId);
            }

            var isRcc = IsRcc();
            if (isRcc)
                encryptionEnabled = false;
#if DEBUG
            encryptionEnabled = false;
#endif
            MultiGetEntry details;
            try
            {
                details = await services.assets.GetAssetCatalogInfo(assetId);
            }
            catch (RecordNotFoundException)
            {
                try
                {
                    var ourId = await services.assets.GetAssetIdFromRobloxAssetId(assetId);
                    assetId = ourId;
                }
                catch (RecordNotFoundException)
                {
                    if (await Services.Cache.distributed.StringGetAsync(invalidIdKey) != null)
                        throw new RobloxException(400, 0, "Asset is invalid or does not exist");

                    try
                    {
                        // Doesn't exist yet, so create it
                        var migrationResult = await MigrateItem.MigrateItemFromRoblox(assetId.ToString(), false, null,
                            new List<Type>()
                            {
                                Type.Image,
                                Type.Audio,
                                Type.Mesh,
                                Type.Lua,
                                Type.Model,
                                Type.Decal,
                                Type.Animation,
                                Type.SolidModel,
                                Type.MeshPart,
                                Type.ClimbAnimation,
                                Type.DeathAnimation,
                                Type.FallAnimation,
                                Type.IdleAnimation,
                                Type.JumpAnimation,
                                Type.RunAnimation,
                                Type.SwimAnimation,
                                Type.WalkAnimation,
                                Type.PoseAnimation,
                            }, default, default, true);
                        assetId = migrationResult.assetId;
                    }
                    catch (AssetTypeNotAllowedException)
                    {
                        // TODO: permanently insert as invalid for AssetTypeNotAllowedException in a table
                        await Services.Cache.distributed.StringSetAsync(invalidIdKey,
                            "{}", TimeSpan.FromDays(7));
                        throw new RobloxException(400, 0, "Asset is invalid or does not exist");
                    }
                    catch (Exception e)
                    {
                        // temporary failure? mark as invalid, but only temporarily
                        Writer.Info(LogGroup.AssetDelivery, "Failed to migrate asset " + assetId + " - " + e.Message + "\n" + e.StackTrace);
                        await Services.Cache.distributed.StringSetAsync(invalidIdKey,
                            "{}", TimeSpan.FromMinutes(1));
                        throw new RobloxException(400, 0, "Asset is invalid or does not exist");
                    }
                }
                details = await services.assets.GetAssetCatalogInfo(assetId);
            }
            if (details.is18Plus && !isRcc && !isBotRequest && !is18OrOver)
                throw new RobloxException(400, 0, "AssetTemporarilyUnavailable");
            // details.moderationStatus != ModerationStatus.ReviewApproved (removing the thing where your asset has to be approved)
            //if (!isRcc && !isBotRequest)
            //    throw new RobloxException(403, 0, "Asset not approved for requester");

            var latestVersion = await services.assets.GetLatestAssetVersion(assetId);
            Stream? assetContent = null;
            switch (details.assetType)
            {
                // Special types
                case Roblox.Models.Assets.Type.TeeShirt:
                    return new MVC.FileContentResult(Encoding.UTF8.GetBytes(ContentFormatters.GetTeeShirt(latestVersion.contentId)), "application/binary");
                case Models.Assets.Type.Shirt:
                    return new MVC.FileContentResult(Encoding.UTF8.GetBytes(ContentFormatters.GetShirt(latestVersion.contentId)), "application/binary");
                case Models.Assets.Type.Pants:
                    return new MVC.FileContentResult(Encoding.UTF8.GetBytes(ContentFormatters.GetPants(latestVersion.contentId)), "application/binary");
                // Types that require no authentication and aren't encrypted
                case Models.Assets.Type.Image:
                case Models.Assets.Type.Special:
                    if (latestVersion.contentUrl != null)
                        assetContent = await services.assets.GetAssetContent(latestVersion.contentUrl);
                    // encryptionEnabled = false;
                    break;
                // Types that require no authentication
                case Models.Assets.Type.Audio:
                case Models.Assets.Type.Mesh:
                case Models.Assets.Type.Hat:
                case Models.Assets.Type.Model:
                case Models.Assets.Type.Decal:
                case Models.Assets.Type.Head:
                case Models.Assets.Type.Face:
                case Models.Assets.Type.Gear:
                case Models.Assets.Type.Badge:
                case Models.Assets.Type.Animation:
                case Models.Assets.Type.Torso:
                case Models.Assets.Type.RightArm:
                case Models.Assets.Type.LeftArm:
                case Models.Assets.Type.RightLeg:
                case Models.Assets.Type.LeftLeg:
                case Models.Assets.Type.Package:
                case Models.Assets.Type.GamePass:
                case Models.Assets.Type.Plugin: // TODO: do plugins need auth?
                case Models.Assets.Type.MeshPart:
                case Models.Assets.Type.HairAccessory:
                case Models.Assets.Type.FaceAccessory:
                case Models.Assets.Type.NeckAccessory:
                case Models.Assets.Type.ShoulderAccessory:
                case Models.Assets.Type.FrontAccessory:
                case Models.Assets.Type.BackAccessory:
                case Models.Assets.Type.WaistAccessory:
                case Models.Assets.Type.ClimbAnimation:
                case Models.Assets.Type.DeathAnimation:
                case Models.Assets.Type.FallAnimation:
                case Models.Assets.Type.IdleAnimation:
                case Models.Assets.Type.JumpAnimation:
                case Models.Assets.Type.RunAnimation:
                case Models.Assets.Type.SwimAnimation:
                case Models.Assets.Type.WalkAnimation:
                case Models.Assets.Type.PoseAnimation:
                case Models.Assets.Type.SolidModel:
                    if (latestVersion.contentUrl is null)
                        throw new RobloxException(400, 0, "BadRequest"); // todo: should we log this?
                    if (details.assetType == Models.Assets.Type.Audio)
                    {
                        // Convert to WAV file since that's what web client requires
                        assetContent = await services.assets.GetAudioContentAsWav(assetId, latestVersion.contentUrl);
                    }
                    else
                    {
                        assetContent = await services.assets.GetAssetContent(latestVersion.contentUrl);
                    }
                    break;
                default:
                    // anything else requires auth
                    var ok = false;
                    if (isRcc)
                    {
                        encryptionEnabled = false;
                        var placeIdHeader = Request.Headers["roblox-place-id"].ToString();
                        long placeId = 0;
                        if (!string.IsNullOrEmpty(placeIdHeader))
                        {
                            try
                            {
                                placeId = long.Parse(Request.Headers["roblox-place-id"].ToString());
                            }
                            catch (FormatException)
                            {
                                // Ignore
                            }
                        }
                        // if rcc is trying to access current place, allow through
                        ok = (placeId == assetId);
                        // If game server is trying to load a new place (current placeId is empty), then allow it
                        if (!ok && details.assetType == Models.Assets.Type.Place && placeId == 0)
                        {
                            // Game server is trying to load, so allow it
                            ok = true;
                        }
                        // If rcc is making the request, but it's not for a place, validate the request:
                        if (!ok)
                        {
                            // Check permissions
                            var placeDetails = await services.assets.GetAssetCatalogInfo(placeId);
                            if (placeDetails.creatorType == details.creatorType &&
                                placeDetails.creatorTargetId == details.creatorTargetId)
                            {
                                // We are authorized
                                ok = true;
                            }
                        }
                    }
                    else
                    {
                        
                        if (userSession != null)
                        {
                        
                            ok = await services.assets.CanUserModifyItem(assetId, userSession.userId);
                            if (!ok)
                            {
                               
                                ok = (details.creatorType == CreatorType.User && details.creatorTargetId == 1);
                            }
#if DEBUG
                            // If staff, allow access in debug builds
                            if (await services.users.IsUserStaff(userSession.userId))
                            {
                                ok = true;
                            }
#endif
                            // Don't encrypt assets being sent to authorized users - they could be trying to download their own place to give to a friend or something
                            if (ok)
                            {
                                encryptionEnabled = false;
                            }
                        }
                    }

                    if (latestVersion.contentUrl != null)
                    {
                        assetContent = await services.assets.GetAssetContent(latestVersion.contentUrl);
                    }

                    break;
            }

            if (assetContent != null)
            {
                return File(assetContent, "application/binary");
            }

            Console.WriteLine("[info] got BadRequest on /asset/ endpoint");
            throw new BadRequestException();
        }

        [HttpGet("Game/GamePass/GamePassHandler.ashx")]
        public async Task<string> GamePassHandler(string Action, long UserID, long PassID)
        {
            if (Action == "HasPass")
            {
                var has = await services.users.GetUserAssets(UserID, PassID);
                return has.Any() ? "True" : "False";
            }

            throw new NotImplementedException();
        }

        [HttpGet("Game/LuaWebService/HandleSocialRequest.ashx")]
        public async Task<string> LuaSocialRequest([Required, MVC.FromQuery] string method, long? playerid = null, long? groupid = null, long? userid = null)
        {
            // TODO: Implement these
            method = method.ToLower();
            if (method == "isingroup" && playerid != null && groupid != null)
            {
                bool isInGroup = false;
                try
                {
                    var group = await services.groups.GetUserRoleInGroup((long)groupid, (long)playerid);
                    if (group.rank != 0)
                        isInGroup = true;
                }
                catch (Exception)
                {

                }

                return "<Value Type=\"boolean\">" + (isInGroup ? "true" : "false") + "</Value>";
            }

            if (method == "getgrouprank" && playerid != null && groupid != null)
            {
                int rank = 0;
                try
                {
                    var group = await services.groups.GetUserRoleInGroup((long)groupid, (long)playerid);
                    rank = group.rank;
                }
                catch (Exception)
                {

                }

                return "<Value Type=\"integer\">" + rank + "</Value>";
            }

            if (method == "getgrouprole" && playerid != null && groupid != null)
            {
                var groups = await services.groups.GetAllRolesForUser((long)playerid);
                foreach (var group in groups)
                {
                    if (group.groupId == groupid)
                    {
                        return group.name;
                    }
                }

                return "Guest";
            }

            if (method == "isfriendswith" && playerid != null && userid != null)
            {
                var status = (await services.friends.MultiGetFriendshipStatus((long)playerid, new[] { (long)userid })).FirstOrDefault();
                if (status != null && status.status == "Friends")
                {
                    return "<Value Type=\"boolean\">True</Value>";
                }
                return "<Value Type=\"boolean\">False</Value>";

            }

            if (method == "isbestfriendswith")
            {
                return "<Value Type\"boolean\">False</value>";
            }

            throw new NotImplementedException();
        }

        [HttpGet("login/negotiate.ashx"), HttpGet("login/negotiateasync.ashx")]
        public async Task Negotiate([Required, MVC.FromQuery] string suggest)
        {
            var domain = Request.Headers["rbxauthenticationnegotiation"].FirstOrDefault();
        }

        [HttpGet("game/getcurrentuser.ashx")]
        public dynamic GetCurrentUserId()
        {
            return "0";
        }

        [HttpGet("universes/validate-place-join")]
        public dynamic UniversePlaceValidator()
        {
            return "true";
        }

        [HttpGet("game/players/{userId:long}")]
        public async Task<dynamic> GetUserById(long userId)
        {
            return "{\"ChatFilter\":\"whitelist\"}";
        }

        [HttpGet("version")]
        public async Task<MVC.FileResult> GetVersion(long id)
        {

            var file = new FileStream(@"C:\services\Bootstrapper\version", FileMode.Open, FileAccess.Read, FileShare.Read, default, FileOptions.Asynchronous);

            return File(file, "application/binary");
        }

        [HttpGet("latestclient")]
        public async Task<MVC.FileResult> GetClient(long id)
        {

            var file = new FileStream(@"C:\services\Bootstrapper\version-88631715556d47989b8f59ebf0340a67-RobloxApp.zip", FileMode.Open, FileAccess.Read, FileShare.Read, default, FileOptions.Asynchronous);

            return File(file, "application/zip");
        }

        [HttpGet("v1/settings/bootstrapperclient")]
        public dynamic ParseBClient()
        {
            return "{\"BetaPlayerLoad\":\"100\",\"CheckIsStudioOutofDate\":\"False\",\"CountersFireImmediately\":\"True\",\"CountersLoad\":\"0\",\"DFFlagBootstrapperHttpToolsDetectJsonParseError\":\"True\",\"DFFlagInfluxEscapeSlashes\":\"True\",\"DFFlagMacBootstrapperDetectJsonParseError\":\"True\",\"DFFlagNewInfluxDb\":\"True\",\"DFStringHttpInfluxDatabase\":\"roblox_bootstrapper\",\"DeprecatePropertyTree\":\"True\",\"DeprecatedOSXMinorVersion\":\"6\",\"EnabledWebRedirect\":\"True\",\"ExeVersion\":\"0.0.0.1\",\"FFlagBetterErrorReporting\":\"True\",\"FFlagChromefixOnlyIfFound\":\"True\",\"FFlagChromefixOpenDefaultPreferences\":\"True\",\"FFlagChromefixOpenFileUnicode\":\"True\",\"FFlagChromefixSkipLocalState\":\"True\",\"FFlagChromefixStrictMatch\":\"True\",\"FFlagDisableUninstallUpdateTask\":\"True\",\"FFlagDontInstallProxyPlugins\":\"True\",\"FFlagDontInstallProxyPluginsMac\":\"True\",\"FFlagDontPatchBootstrapperInfluxFlags\":\"True\",\"FFlagIncludeTranslationsFolder\":\"True\",\"FFlagIncludeTranslationsFolderStudio\":\"True\",\"FFlagJoinTimeCounters\":\"False\",\"FFlagMacUseNewVersionFetch4\":\"True\",\"FFlagRelaunchAfterInstallPassUrlString\":\"True\",\"FFlagThrowLastErrorAlwaysThrow\":\"True\",\"FFlagUninstallProxyPluginsOnInstall\":\"True\",\"FFlagUninstallProxyPluginsOnInstallMac\":\"True\",\"FFlagUseCountryCodesForAnalyticsLocation\":\"True\",\"FFlagUseNewVersionFetch4\":\"True\",\"FFlagUseNewVersionFetchFixed2\":\"False\",\"FIntInfluxReportExceptionPermyriad\":\"10000\",\"ForceSilentMode\":\"False\",\"GoogleAnalyticsAccountPropertyID\":\"UA-43420590-16\",\"GuidVersion\":\"a4781a8a67bb4885\",\"HardcodeCdnHost\":\"False\",\"InfluxDatabase\":\"roblox_bootstrapper\",\"InfluxHundredthsPercentage\":\"0\",\"InfluxInstallHundredthsPercentage\":\"10000\",\"IsPreDeployOn\":\"False\",\"LaunchWithLegacyFlagEnabled\":\"100\",\"NeedInstallBgTask\":\"False\",\"NeedRunBgTask\":\"False\",\"PerModeLoggingEnabled\":\"True\",\"PreVersion\":\"\",\"ReplaceCdnTxt\":\"True\",\"ReplaceStaticCdnHost\":\"True\",\"ShowInstallSuccessPrompt\":\"True\",\"UseCdn\":\"True\",\"UseFastStartup\":\"True\",\"UseNewCdn\":\"True\",\"UseNewDeprecatedOSXCheck\":\"True\",\"UseNewInfluxBootstrapper\":\"True\",\"UseRelaunchEmptyArgs\":\"True\",\"ValidateInstalledExeVersion\":\"True\"}";
        }

        [HttpGet("v1/settings/client")]
        public dynamic ParseClient()
        {
            return "{\"FFlagCoreScriptShowVisibleAgeV2\":\"True\",\"StudioEarlyCookieConstraintCheckGlobal\":\"False\",\"FFlagCoreScriptShowVisibleAge\":\"True\",\"DFFlagFindFirstChildOfClassEnabled\":\"True\",\"FFlagStudioCSGAssets\":\"True\",\"FFlagCSGLoadBlocking\":\"False\",\"FFlagCSGPhysicsLevelOfDetailEnabled\":\"True\",\"FFlagFormFactorDeprecated\":\"False\",\"FFlagFontSmoothScalling\":\"True\",\"FFlagAlternateFontKerning\":\"True\",\"FFlagFontSourceSans\":\"True\",\"FFlagRenderNewFonts\":\"True\",\"FFlagDMFeatherweightEnabled\":\"True\",\"FFlagRenderFeatherweightEnabled\":\"True\",\"FFlagRenderFeatherweightUseGeometryGenerator\":\"True\",\"FFlagScaleExplosionLifetime\":\"True\",\"FFlagEnableNonleathalExplosions\":\"True\",\"DFFlagHttpCurlHandle301\":\"True\",\"FFlagSearchToolboxByDataModelSearchString\":\"True\",\"FFlagClientABTestingEnabled\":\"False\",\"FFlagStudioSmoothTerrainForNewPlaces\":\"True\",\"FFlagUsePGSSolver\":\"True\",\"FFlagSimplifyKeyboardInputPath\":\"False\",\"FFlagNewInGameDevConsole\":\"True\",\"FFlagTextFieldUTF8\":\"True\",\"FFlagTypesettersReleaseResources\":\"True\",\"FFlagLuaBasedBubbleChat\":\"True\",\"FFlagUseCanManageApiToDetermineConsoleAccess\":\"False\",\"FFlagConsoleCodeExecutionEnabled\":\"True\",\"DFFlagCustomEmitterInstanceEnabled\":\"True\",\"FFlagCustomEmitterRenderEnabled\":\"True\",\"FFlagCustomEmitterLuaTypesEnabled\":\"True\",\"FFlagStudioInSyncWebKitAuthentication\":\"False\",\"FFlagGlowEnabled\":\"True\",\"FFlagUseNewAppBridgeInputWindows\":\"False\",\"DFFlagUseNewFullscreenLogic\":\"True\",\"FFlagRenderMaterialsOnMobile\":\"True\",\"FFlagMaterialPropertiesEnabled\":\"True\",\"FFlagSurfaceLightEnabled\":\"True\",\"FFlagStudioPropertyErrorOutput\":\"True\",\"DFFlagUseR15Character\":\"True\",\"DFFlagEnableHipHeight\":\"True\",\"DFFlagUseStarterPlayerCharacter\":\"True\",\"DFFlagFilteringEnabledDialogFix\":\"True\",\"FFlagCSGMeshColorToolsEnabled\":\"True\",\"FFlagStudioEnableGameAnimationsTab\":\"True\",\"DFFlagScriptExecutionContextApi\":\"True\",\"FFlagStudioVariableIntellesense\":\"True\",\"FFlagLuaDebugger\":\"True\",\"FFlagUseUserListMenu\":\"True\",\"FFlagEnableSetCoreTopbarEnabled\":\"True\",\"FFlagPlayerDropDownEnabled\":\"True\",\"FFlagSetCoreMoveChat\":\"True\",\"FFlagSetCoreDisableChatBar\":\"True\",\"FFlagGraphicsGL3\":\"True\",\"DFFlagUserUseLuaVehicleController\":\"True\",\"FFlagTextBoxUnicodeAware\":\"True\",\"FFlagLetLegacyScriptsWork\":\"True\",\"FFlagDep\":\"True\",\"DFFlagDisableBackendInsertConnection\":\"True\",\"FFlagPhysicsAnalyzerEnabled\":\"True\",\"DFFlagGetGroupsAsyncEnabled\":\"True\",\"DFFlagGetFocusedTextBoxEnabled\":\"True\",\"DFFlagTextBoxIsFocusedEnabled\":\"True\",\"DFFlagGetCharacterAppearanceEnabled\":\"True\",\"FFlagNewLayoutAndConstraintsEnabled\":\"True\",\"GoogleAnalyticsAccountPropertyID\":\"UA-43420590-3\",\"GoogleAnalyticsAccountPropertyIDPlayer\":\"UA-43420590-14\",\"AllowVideoPreRoll\":\"True\",\"FLogAsserts\":\"0\",\"FLogCloseDataModel\":\"3\",\"CaptureQTStudioCountersEnabled\":\"True\",\"CaptureMFCStudioCountersEnabled\":\"True\",\"CaptureCountersIntervalInMinutes\":\"5\",\"FLogServiceVectorResize\":\"4\",\"FLogServiceCreation\":\"4\",\"AxisAdornmentGrabSize\":\"12\",\"FFlagProcessAllPacketsPerStep\":\"True\",\"FFlagUS14116\":\"True\",\"FFlagBlockBlockNarrowPhaseRefactor\":\"True\",\"FFlagEnableRubberBandSelection\":\"True\",\"FFlagQtStudioScreenshotEnabled\":\"True\",\"FFlagFixNoPhysicsGlitchWithGyro\":\"True\",\"FLogFullRenderObjects\":\"0\",\"PublishedProjectsPageHeight\":\"535\",\"PublishedProjectsPageUrl\":\"/ide/publish\",\"StartPageUrl\":\"/ide/welcome\",\"FFlagOpenNewWindowsInDefaultBrowser\":\"True\",\"FFlagOnScreenProfiler\":\"True\",\"FFlagInitializeNewPlace\":\"True\",\"PrizeAssetIDs\":\"\",\"PrizeAwarderURL\":\"/ostara/boon\",\"MinNumberScriptExecutionsToGetPrize\":\"500\",\"FFlagDebugCrashEnabled\":\"True\",\"FLogHangDetection\":\"3\",\"FFlagCharAnimationStats\":\"False\",\"FFlagRenderOpenGLForcePOTTextures\":\"True\",\"FFlagUseNewCameraZoomPath\":\"True\",\"FFlagQTStudioPublishFailure\":\"True\",\"ExcludeContactWithInteriorTerrainMinusYFace\":\"True\",\"FFlagFixUphillClimb\":\"True\",\"FFlagUseAveragedFloorHeight\":\"True\",\"PublishedProjectsPageWidth\":\"950\",\"FFlagRenderFastClusterEverywhere\":\"True\",\"FLogPlayerShutdownLuaTimeoutSeconds\":\"1\",\"FFlagQtFixToolDragging\":\"True\",\"FFlagSelectPartOnUndoRedo\":\"True\",\"FFlagStatusBarProgress\":\"True\",\"FFlagStudioCheckForUpgrade\":\"True\",\"FFlagStudioInsertModelCounterEnabled\":\"True\",\"FFlagStudioAuthenticationFailureCounterEnabled\":\"True\",\"FFlagRenderCheckTextureContentProvider\":\"True\",\"FFlagRenderLightGridEnabled\":\"True\",\"FFlagStudioLightGridAPIVisible\":\"True\",\"FFlagBetterSleepingJobErrorComputation\":\"True\",\"FLogDXVideoMemory\":\"4\",\"FFlagRenderNoLegacy\":\"True\",\"FFlagStudioLightGridOnForNewPlaces\":\"True\",\"FFlagPhysicsSkipRedundantJoinAll\":\"True\",\"FFlagTerrainOptimizedLoad\":\"True\",\"FFlagTerrainOptimizedStorage\":\"True\",\"FFlagTerrainOptimizedCHS\":\"True\",\"FFlagRenderGLES2\":\"True\",\"FFlagStudioMacAddressValidationEnabled\":\"True\",\"FFlagDoNotPassSunkEventsToPlayerMouse\":\"True\",\"FFlagQtAutoSave\":\"True\",\"FFlagRenderLoopExplicit\":\"True\",\"FFlagStudioUseBinaryFormatForPlay\":\"True\",\"FFlagPhysicsRemoveWorldAssemble_US16512\":\"True\",\"FFlagNativeSafeChatRendering\":\"True\",\"FFlagRenderNewMegaCluster\":\"True\",\"FFlagAutoJumpForTouchDevices\":\"True\",\"FLogOutlineBrightnessMin\":\"50\",\"FLogOutlineBrightnessMax\":\"160\",\"FLogOutlineThickness\":\"40\",\"FFlagDE5511FixEnabled\":\"True\",\"FFlagDE4423Fixed\":\"True\",\"FFlagSymmetricContact\":\"True\",\"FFlagLocalMD5\":\"True\",\"FFlagStudioCookieParsingDisabled\":\"False\",\"FFlagLastWakeTimeSleepingJobError\":\"True\",\"FFlagPhysicsAllowAutoJointsWithSmallParts_DE6056\":\"True\",\"FFlagPhysicsLockGroupDraggerHitPointOntoSurface_DE6174\":\"True\",\"FFlagOutlineControlEnabled\":\"True\",\"FFlagAllowCommentedScriptSigs\":\"True\",\"FFlagDataModelUseBinaryFormatForSave\":\"True\",\"FFlagStudioUseBinaryFormatForSave\":\"True\",\"FFlagDebugAdornableCrash\":\"True\",\"FFlagOverlayDataModelEnabled\":\"True\",\"DFFlagFixInstanceParentDesyncBug\":\"True\",\"FFlagPromoteAssemblyModifications\":\"False\",\"DFFlagCreateHumanoidRootNode\":\"True\",\"FFlagStudioCookieDesegregation\":\"True\",\"FFlagResponsiveJump\":\"True\",\"FFlagGoogleAnalyticsTrackingEnabled\":\"True\",\"FFlagNoCollideLadderFilter\":\"True\",\"FFlagFlexibleTipping\":\"True\",\"FFlagUseStrongerBalancer\":\"True\",\"FFlagClampControllerVelocityMag\":\"True\",\"DFFlagUseSaferChatMetadataLoading\":\"True\",\"FFlagSinkActiveGuiObjectMouseEvents\":\"False\",\"FLogLuaBridge\":\"2\",\"DFFlagPromoteAssemblyModifications\":\"True\",\"FFlagDeferredContacts\":\"True\",\"FFlagFRMUse60FPSLockstepTable\":\"True\",\"FFlagFRMAdjustForMultiCore\":\"True\",\"FFlagPhysics60HZ\":\"True\",\"FFlagQtRightClickContextMenu\":\"True\",\"FFlagUseTopmostSettingToBringWindowToFront\":\"True\",\"FFlagNewLightAPI\":\"True\",\"FFlagRenderLightGridShadows\":\"True\",\"FFlagRenderLightGridShadowsSmooth\":\"True\",\"DFFlagSanitizeKeyframeUrl\":\"True\",\"DFFlagDisableGetKeyframeSequence\":\"False\",\"FFlagCreateServerScriptServiceInStudio\":\"True\",\"FFlagCreateServerStorageInStudio\":\"True\",\"FFlagCreateReplicatedStorageInStudio\":\"True\",\"FFlagFilterEmoteChat\":\"True\",\"DFFlagUseCharacterRootforCameraTarget\":\"True\",\"FFlagImageRectEnabled\":\"True\",\"FFlagNewWaterMaterialEnable\":\"True\",\"DFFlagUserHttpAPIEnabled\":\"True\",\"DFIntUserHttpAccessUserId0\":\"0\",\"FFlagUserHttpAPIVisible\":\"True\",\"FFlagCameraChangeHistory\":\"True\",\"FFlagDE4640Fixed\":\"True\",\"FFlagShowStreamingEnabledProp\":\"True\",\"FFlagOptimizedDragger\":\"True\",\"FFlagRenderNewMaterials\":\"True\",\"FFlagRenderAnisotropy\":\"True\",\"FFlagStudioInitializeViewOnPaint\":\"True\",\"DFFlagPartsStreamingEnabled\":\"True\",\"FFlagStudioLuaDebugger\":\"True\",\"FFlagStudioLocalSpaceDragger\":\"True\",\"FFlagGuiRotationEnabled\":\"True\",\"FFlagDataStoreEnabled\":\"True\",\"DFFlagDisableTeleportConfirmation\":\"True\",\"DFFlagAllowTeleportFromServer\":\"True\",\"DFFlagNonBlockingTeleport\":\"True\",\"FFlagD3D9CrashOnError\":\"False\",\"FFlagRibbonBarEnabled\":\"True\",\"SFFlagInfiniteTerrain\":\"True\",\"FFlagStudioScriptBlockAutocomplete\":\"True\",\"FFlagRenderFixAnchoredLag\":\"True\",\"DFFlagAllowAllUsersToUseHttpService\":\"True\",\"GoogleAnalyticsAccountPropertyIDClient\":\"\",\"FFlagSurfaceGuiVisible\":\"True\",\"FFlagStudioIntellesenseEnabled\":\"True\",\"FFlagAsyncPostMachineInfo\":\"True\",\"FFlagModuleScriptsVisible\":\"True\",\"FFlagModelPluginsEnabled\":\"True\",\"FFlagGetUserIdFromPluginEnabled\":\"True\",\"FFlagStudioPluginUIActionEnabled\":\"True\",\"DFFlagRemoveAdornFromBucketInDtor\":\"True\",\"FFlagRapidJSONEnabled\":\"True\",\"DFFlagDE6959Fixed\":\"True\",\"DFFlagScopedMutexOnJSONParser\":\"True\",\"FFlagSupressNavOnTextBoxFocus\":\"True\",\"DFFlagExplicitPostContentType\":\"True\",\"DFFlagAddPlaceIdToAnimationRequests\":\"True\",\"FFlagCreatePlaceEnabled\":\"True\",\"DFFlagClientAdditionalPOSTHeaders\":\"True\",\"FFlagEnableAnimationExport\":\"True\",\"DFFlagAnimationAllowProdUrls\":\"True\",\"FFlagGetUserIDFromPluginEnabled\":\"True\",\"FFlagStudioContextualHelpEnabled\":\"True\",\"FFlagLogServiceEnabled\":\"True\",\"FFlagQtPlaySoloOptimization\":\"True\",\"FFlagStudioBuildGui\":\"True\",\"DFFlagListenForZVectorChanges\":\"True\",\"DFFlagUserInputServiceProcessOnRender\":\"True\",\"FFlagDE7421Fixed\":\"True\",\"FFlagStudioExplorerActionsEnabledInScriptView\":\"True\",\"FFlagHumanoidNetworkOptEnabled\":\"False\",\"DFFlagEnableNPCServerAnimation\":\"True\",\"DFFlagDataStoreUseUForGlobalDataStore\":\"True\",\"DFFlagDataStoreAllowedForEveryone\":\"True\",\"DFFlagBadTypeOnConnectErrorEnabled\":\"True\",\"FFlagStudioRemoveUpdateUIThread\":\"True\",\"FFlagPhysicsSkipUnnecessaryContactCreation\":\"False\",\"FFlagUseNewHumanoidCache\":\"True\",\"FFlagSecureReceiptsBackendEnabled\":\"True\",\"FFlagOrderedDataStoreEnabled\":\"True\",\"FFlagStudioLuaDebuggerGA\":\"True\",\"FFlagNPSSetScriptDocsReadOnly\":\"True\",\"FFlagRDBGHashStringComparison\":\"True\",\"FFlagStudioDebuggerVisitDescendants\":\"True\",\"FFlagDeprecateScriptInfoService\":\"True\",\"FFlagIntellisenseScriptContextDatamodelSearchingEnabled\":\"True\",\"FFlagSecureReceiptsFrontendEnabled\":\"True\",\"DFFlagCreatePlaceEnabledForEveryone\":\"True\",\"FFlagCreatePlaceInPlayerInventoryEnabled\":\"True\",\"DFFlagAddRequestIdToDeveloperProductPurchases\":\"True\",\"DFFlagUseYPCallInsteadOfPCallEnabled\":\"True\",\"FFlagStudioMouseOffsetFixEnabled\":\"True\",\"DFFlagPlaceValidation\":\"True\",\"FFlagReconstructAssetUrl\":\"True\",\"FFlagUseNewSoundEngine\":\"True\",\"FIntMinMillisecondLengthForLongSoundChannel\":\"5000\",\"FFlagStudioHideInsertedServices\":\"True\",\"FFlagStudioAlwaysSetActionEnabledState\":\"True\",\"FFlagRenderNew\":\"True\",\"FIntRenderNewPercentWin\":\"100\",\"FIntRenderNewPercentMac\":\"100\",\"FLogGraphics\":\"6\",\"DFFlagDisallowHopperServerScriptReplication\":\"True\",\"FFlagInterpolationFix\":\"False\",\"FFlagHeartbeatAt60Hz\":\"False\",\"DFFlagFixProcessReceiptValueTypes\":\"True\",\"DFFlagPhysicsSkipUnnecessaryContactCreation\":\"True\",\"FFlagStudioLiveCoding\":\"True\",\"FFlagPlayerHumanoidStep60Hz\":\"True\",\"DFFlagCrispFilteringEnabled\":\"False\",\"SFFlagProtocolSynchronization\":\"True\",\"FFlagUserInputServicePipelineStudio\":\"True\",\"FFlagUserInputServicePipelineWindowsClient\":\"True\",\"FFlagUserInputServicePipelineMacClient\":\"True\",\"FFlagStudioKeyboardMouseConfig\":\"True\",\"DFFlagLogServiceEnabled\":\"True\",\"DFFlagLoadAnimationsThroughInsertService\":\"True\",\"FFlagFRMFogEnabled\":\"True\",\"FLogBrowserActivity\":\"3\",\"DFFlagPhysicsPacketAlwaysUseCurrentTime\":\"True\",\"FFlagFixedStudioRotateTool\":\"True\",\"FFlagRibbonBarEnabledGA\":\"True\",\"FFlagRenderSafeChat\":\"False\",\"DFFlagPhysicsAllowSimRadiusToDecreaseToOne\":\"True\",\"DFFlagPhysicsAggressiveSimRadiusReduction\":\"True\",\"DFFlagLuaYieldErrorNoResumeEnabled\":\"True\",\"DFFlagEnableJointCache\":\"False\",\"DFFlagOnCloseTimeoutEnabled\":\"True\",\"FFlagStudioQuickInsertEnabled\":\"True\",\"FFlagStudioPropertiesRespectCollisionToggle\":\"True\",\"FFlagTweenServiceUsesRenderStep\":\"True\",\"FFlagUseNewSoundEngine3dFix\":\"True\",\"FFlagDebugUseDefaultGlobalSettings\":\"True\",\"FFlagStudioMiddleMouseTrackCamera\":\"False\",\"FFlagTurnOffiOSNativeControls\":\"True\",\"DFFlagUseNewHumanoidHealthGui\":\"True\",\"DFFlagLoggingConsoleEnabled\":\"True\",\"DFFlagAllowModuleLoadingFromAssetId\":\"True\",\"FFlagStudioZoomExtentsExplorerFixEnabled\":\"True\",\"FFlagLuaDebuggerBreakOnError\":\"True\",\"FFlagRetentionTrackingEnabled\":\"True\",\"FFlagShowAlmostAllItemsInExplorer\":\"True\",\"FFlagStudioFindInAllScriptsEnabled\":\"True\",\"FFlagImprovedNameOcclusion\":\"True\",\"FFlagHumanoidMoveToDefaultValueEnabled\":\"True\",\"FFlagEnableDisplayDistances\":\"True\",\"FFlagUseMinMaxZoomDistance\":\"True\",\"SFFlagAllowPhysicsPacketCompression\":\"False\",\"FFlagStudioOneClickColorPickerEnabled\":\"True\",\"DFFlagHumanoidMoveToDefaultValueEnabled\":\"True\",\"VideoPreRollWaitTimeSeconds\":\"45\",\"FFlagBalancingRateLimit\":\"True\",\"FFlagLadderCheckRate\":\"True\",\"FFlagStateSpecificAutoJump\":\"True\",\"SFFlagOneWaySimRadiusReplication\":\"True\",\"DFFlagApiDictionaryCompression\":\"True\",\"SFFlagPathBasedPartMovement\":\"True\",\"FFlagEnsureInputIsCorrectState\":\"False\",\"DFFlagLuaLoadStringStrictSecurity\":\"True\",\"DFFlagCrossPacketCompression\":\"True\",\"FFlagWorkspaceLoadStringEnabledHidden\":\"True\",\"FFlagStudioPasteAsSiblingEnabled\":\"True\",\"FFlagStudioDuplicateActionEnabled\":\"True\",\"FFlagPreventInterpolationOnCFrameChange\":\"True\",\"FLogNetworkPacketsReceive\":\"5\",\"FFlagPlayPauseFix\":\"True\",\"DFFlagCrashOnNetworkPacketError\":\"False\",\"FFlagHumanoidStateInterfaces\":\"True\",\"FFlagRenderDownloadAssets\":\"True\",\"FFlagBreakOnErrorConfirmationDialog\":\"True\",\"FFlagStudioAnalyticsEnabled\":\"True\",\"FFlagAutoRotateFlag\":\"True\",\"DFFlagUseImprovedLadderClimb\":\"True\",\"FFlagUseCameraOffset\":\"True\",\"FFlagRenderBlobShadows\":\"True\",\"DFFlagWebParserDisableInstances\":\"False\",\"FFlagStudioNewWiki\":\"True\",\"DFFlagLogPacketErrorDetails\":\"False\",\"FFlagLimitHorizontalDragForce\":\"True\",\"DFFlagCreateSeatWeldOnServer\":\"True\",\"FFlagGraphicsUseRetina\":\"True\",\"FFlagDynamicEnvmapEnabled\":\"True\",\"DFFlagDeferredTouchReplication\":\"True\",\"DFFlagCreatePlayerGuiEarlier\":\"True\",\"DFFlagProjectileOwnershipOptimization\":\"True\",\"DFFlagLoadSourceForCoreScriptsBeforeInserting\":\"False\",\"GoogleAnalyticsLoadStudio\":\"1\",\"DFFlagTaskSchedulerFindJobOpt\":\"True\",\"SFFlagPreventInterpolationOnCFrameChange\":\"True\",\"DFIntNumPhysicsPacketsPerStep\":\"2\",\"DFFlagDataStoreUrlEncodingEnabled\":\"True\",\"FFlagShowWebPlaceNameOnTabWhenOpeningFromWeb\":\"True\",\"DFFlagTrackTimesScriptLoadedFromLinkedSource\":\"True\",\"FFlagToggleDevConsoleThroughChatCommandEnabled\":\"True\",\"FFlagEnableFullMonitorsResolution\":\"True\",\"DFFlagAlwaysUseHumanoidMass\":\"True\",\"DFFlagUseStrongerGroundControl\":\"True\",\"DFFlagCorrectlyReportSpeedOnRunStart\":\"True\",\"FFlagLuaDebuggerImprovedToolTip\":\"True\",\"FFlagLuaDebuggerPopulateFuncName\":\"True\",\"FFlagLuaDebuggerNewCodeFlow\":\"True\",\"DFFlagValidateCharacterAppearanceUrl\":\"false\",\"FFlagStudioQuickAccessCustomization\":\"True\",\"DFFlagTaskSchedulerUpdateJobPriorityOnWake\":\"True\",\"DFFlagTaskSchedulerNotUpdateErrorOnPreStep\":\"True\",\"FFlagWikiSelectionSearch\":\"True\",\"DFIntTaskSchedularBatchErrorCalcFPS\":\"1200\",\"FFlagSuppressNavOnTextBoxFocus\":\"False\",\"FFlagEnabledMouseIconStack\":\"True\",\"DFFlagFastClone\":\"True\",\"DFFlagLuaNoTailCalls\":\"True\",\"DFFlagFilterStreamingProps\":\"True\",\"DFFlagNetworkOwnerOptEnabled\":\"True\",\"DFFlagPathfindingEnabled\":\"True\",\"FFlagEnableiOSSettingsLeave\":\"True\",\"FFlagUseFollowCamera\":\"True\",\"FFlagDefaultToFollowCameraOnTouch\":\"True\",\"DFFlagAllowMoveToInMouseLookMove\":\"True\",\"DFFlagAllowHumanoidDecalTransparency\":\"True\",\"DFFlagSupportCsrfHeaders\":\"True\",\"DFFlagConfigureInsertServiceFromSettings\":\"True\",\"FFlagPathfindingClientComputeEnabled\":\"True\",\"DFFlagLuaResumeSupportsCeeCalls\":\"True\",\"DFFlagPhysicsSenderErrorCalcOpt\":\"True\",\"DFFlagClearPlayerReceivingServerLogsOnLeave\":\"True\",\"DFFlagConsoleCodeExecutionEnabled\":\"True\",\"DFFlagCSGDictionaryReplication\":\"True\",\"FFlagCSGToolsEnabled\":\"True\",\"FFlagCSGDictionaryServiceEnabled\":\"True\",\"FFlagCSGMeshRenderEnable\":\"True\",\"FFlagCSGChangeHistory\":\"True\",\"FFlagCSGMeshColorEnable\":\"True\",\"FFlagCSGScaleEnabled\":\"True\",\"FFlagCylinderUsesConstantTessellation\":\"True\",\"FFlagStudioDraggersScaleFixes\":\"True\",\"FFlagCSGDecalsEnabled\":\"True\",\"FFlagCSGMigrateChildData\":\"True\",\"SFFlagBinaryStringReplicationFix\":\"True\",\"FFlagHummanoidScaleEnable\":\"True\",\"FFlagStudioDataModelIsStudioFix\":\"True\",\"DFFlagWebParserEnforceASCIIEnabled\":\"True\",\"DFFlagScriptDefaultSourceIsEmpty\":\"True\",\"FFlagFixCaptureFocusInput\":\"True\",\"FFlagFireUserInputServiceEventsAfterDMEvents\":\"True\",\"FFlagVectorErrorOnNilArithmetic\":\"True\",\"DFFlagUseImageColor\":\"True\",\"FFlagStopNoPhysicsStrafe\":\"True\",\"DFFlagDebugLogNetworkErrorToDB\":\"False\",\"FFlagLowQMaterialsEnable\":\"True\",\"FFLagEnableFullMonitorsResolution\":\"True\",\"FFlagStudioChildProcessCleanEnabled\":\"True\",\"DFFlagAllowFullModelsWhenLoadingModules\":\"True\",\"DFFlagRealWinInetHttpCacheBypassingEnabled\":\"True\",\"FFlagNewUniverseInfoEndpointEnabled\":\"True\",\"FFlagGameExplorerEnabled\":\"True\",\"FFlagStudioUseBinaryFormatForModelPublish\":\"True\",\"FFlagGraphicsFeatureLvlStatsEnable\":\"True\",\"FFlagStudioEnableWebKitPlugins\":\"True\",\"DFFlagSendHumanoidTouchedSignal\":\"True\",\"DFFlagReduceHumanoidBounce\":\"True\",\"DFFlagUseNewSounds\":\"True\",\"FFlagFixHumanoidRootPartCollision\":\"True\",\"FFlagEnableAndroidMenuLeave\":\"True\",\"FFlagOnlyProcessGestureEventsWhenSunk\":\"True\",\"FFlagAdServiceReportImpressions\":\"True\",\"FFlagStudioUseExtendedHTTPTimeout\":\"True\",\"FFlagStudioSeparateActionByActivationMethod\":\"False\",\"DFFlagPhysicsSenderThrottleBasedOnBufferHealth\":\"True\",\"DFFlagGetGroupInfoEnabled\":\"True\",\"DFFlagGetGroupRelationsEnabled\":\"True\",\"SFFlagTopRepContSync\":\"True\",\"FFlagStudioUseBinaryFormatForModelSave\":\"True\",\"EnableFullMonitorsResolution\":\"True\",\"DFFlagRenderSteppedServerExceptionEnabled\":\"True\",\"FFlagUseWindowSizeFromGameSettings\":\"True\",\"DFFlagCheckStudioApiAccess\":\"True\",\"FFlagGameExplorerPublishEnabled\":\"True\",\"DFFlagKeepXmlIdsBetweenLoads\":\"True\",\"DFFlagReadXmlCDataEnabled\":\"True\",\"FFlagStudioRemoveToolSounds\":\"True\",\"FFlagStudioOneStudGridDefault\":\"True\",\"FFlagStudioPartSymmetricByDefault\":\"True\",\"FFlagStudioIncreasedBaseplateSize\":\"True\",\"FFlagSkipSilentAudioOps\":\"True\",\"SFFlagGuid64Bit\":\"False\",\"FIntValidateLauncherPercent\":\"100\",\"FFlagCSGDataLossFixEnabled\":\"True\",\"DFStringRobloxAnalyticsURL\":\"http://analytics.calvyy.xyz/\",\"DFFlagRobloxAnalyticsTrackingEnabled\":\"True\",\"FFlagStudioOpenLastSaved\":\"False\",\"FFlagStudioShowTutorialsByDefault\":\"True\",\"FFlagStudioForceToolboxSize\":\"True\",\"FFlagStudioExplorerDisabledByDefault\":\"True\",\"FFlagStudioDefaultWidgetSizeChangesEnabled\":\"True\",\"FFlagStudioUseScriptAnalyzer\":\"True\",\"FFlagNoClimbPeople\":\"True\",\"DFFlagAnimationFormatAssetId\":\"True\",\"FFlagGetCorrectScreenResolutionFaster\":\"True\",\"DFFlagFixTouchEndedReporting\":\"False\",\"FFlagStudioTeleportPlaySolo\":\"True\",\"FFlagStudioDE7928FixEnabled\":\"True\",\"FFlagDE8768FixEnabled\":\"True\",\"FFlagStudioDE9108FixEnabled\":\"True\",\"FFlagStudioPlaySoloMapDebuggerData\":\"True\",\"FFlagLuaDebuggerCloneDebugger\":\"True\",\"FFlagRenderLightgridInPerformEnable\":\"True\",\"SFFlagStateBasedAnimationReplication\":\"True\",\"FFlagVolumeControlInGameEnabled\":\"True\",\"FFlagGameConfigurerUseStatsService\":\"True\",\"FFlagStudioUseHttpAuthentication\":\"True\",\"FFlagDetectTemplatesWhenSettingUpGameExplorerEnabled\":\"True\",\"FFlagEntityNameEditingEnabled\":\"True\",\"FFlagNewCreatePlaceFlowEnabled\":\"True\",\"FFlagFakePlayableDevices\":\"False\",\"FFlagMutePreRollSoundService\":\"True\",\"DFFlagBodyMoverParentingFix\":\"True\",\"DFFlagBroadcastServerOnAllInterfaces\":\"True\",\"HttpUseCurlPercentageWinClient\":\"100\",\"HttpUseCurlPercentageMacClient\":\"100\",\"HttpUseCurlPercentageWinStudio\":\"100\",\"HttpUseCurlPercentageMacStudio\":\"100\",\"SFFlagReplicatedFirstEnabled\":\"True\",\"DFFlagCSGShrinkForMargin\":\"True\",\"FFlagPhysicsBulletConnectorPointRecalc\":\"True\",\"DFIntBulletContactBreakThresholdPercent\":\"200\",\"DFIntBulletContactBreakOrthogonalThresholdPercent\":\"200\",\"FFlagPhysicsBulletConnectorMatching\":\"True\",\"FFlagPhysicsBulletUseProximityMatching\":\"False\",\"FFlagPhysicsCSGUsesBullet\":\"True\",\"DFFlagCSGPhysicsDeserializeRefactor\":\"True\",\"FFlagWedgeEnableDecalOnTop\":\"True\",\"FFlagFrustumTestGUI\":\"True\",\"FFlagFeatureLvlsDX11BeforeDeviceCreate\":\"True\",\"FFlagStudioPasteSyncEnabled\":\"True\",\"FFlagResetMouseCursorOnToolUnequip\":\"True\",\"DFFlagUpdateCameraTarget\":\"True\",\"DFFlagFixGhostClimb\":\"True\",\"DFFlagUseStarterPlayer\":\"True\",\"FFlagStudioFindCrashFixEnabled\":\"True\",\"FFlagFixPartOffset\":\"True\",\"DFFlagLuaCloseUpvalues\":\"True\",\"FFlagRenderTextureCompositorUseBudgetForSize\":\"True\",\"FFlagAllowOutOfBoxAssets\":\"False\",\"FFlagRemoveTintingWhenActiveIsFalseOnImageButton\":\"True\",\"FFlagStudioModuleScriptDefaultContents\":\"True\",\"FFlagStudioHomeKeyChangeEnabled\":\"True\",\"FFlagStudioOpenStartPageForLogin\":\"True\",\"FFlagStudioNativeKeepSavedChanges\":\"True\",\"FFlagSerializeCurrentlyOpenPlaceWhenPublishingGame\":\"True\",\"FFlagGameNameLabelEnabled\":\"True\",\"FFlagStudioValidateBootstrapper\":\"True\",\"FFlagRakNetReadFast\":\"True\",\"DFFlagPhysicsSenderSleepingUpdate\":\"True\",\"FFlagUseShortShingles\":\"True\",\"FFlagKKTChecks\":\"False\",\"DFFlagUseApiProxyThrottling\":\"True\",\"DFFlagValidateSetCharacter\":\"True\",\"DFFlagUpdateHumanoidSimBodyInComputeForce\":\"True\",\"DFFlagNetworkPendingItemsPreserveTimestamp\":\"True\",\"FFlagStudioCSGRotationalFix\":\"True\",\"FFlagNewLoadingScreen\":\"True\",\"FFlagScrollingFrameOverridesButtonsOnTouch\":\"True\",\"DFFlagStreamLargeAudioFiles\":\"True\",\"DFFlagNewLuaChatScript\":\"True\",\"DFFlagLoopedDefaultHumanoidAnimation\":\"True\",\"FFlagSoundsRespectDelayedStop\":\"False\",\"DFFlagCSGPhysicsErrorCatchingEnabled\":\"True\",\"DFFlagFireStoppedAnimSignal\":\"True\",\"FFlagStudioFixToolboxReload\":\"True\",\"FFlagCSGDecalsV2\":\"True\",\"FFlagLocalOptimizer\":\"True\",\"DFFlagClearFailedUrlsWhenClearingCacheEnabled\":\"True\",\"DFFlagSupportNamedAssetsShortcutUrl\":\"True\",\"DFFlagUseW3CURIParser\":\"True\",\"DFFlagContentProviderHttpCaching\":\"True\",\"FFlagNoWallClimb\":\"False\",\"FFlagSmoothMouseLock\":\"False\",\"DFFlagCSGPhysicsNanPrevention\":\"True\",\"FFlagStudioDE9818FixEnabled\":\"True\",\"FFlagGameExplorerImagesEnabled\":\"True\",\"FFlagStudioInsertOrientationFix\":\"True\",\"FFlagStudioTabOrderingEnabled\":\"True\",\"FFlagFramerateDeviationDroppedReport\":\"True\",\"FFlagModuleScriptsPerVmEnabled\":\"False\",\"FFlagGameExplorerImagesInsertEnabled\":\"True\",\"FFlagTexturePropertyWidgetEnabled\":\"True\",\"FFlagReloadAllImagesOnDataReload\":\"True\",\"FFlagModuleScriptsPerVmEnabledFix2\":\"True\",\"DFFlagFixBufferZoneContainsCheck\":\"False\",\"FFlagStudioPlaceAssetFromToolbox\":\"True\",\"FFlagChannelMasterMuting\":\"True\",\"FFlagStudioUseDelayedSyntaxCheck\":\"True\",\"FFlagStudioCommandLineSaveEditText\":\"True\",\"FFlagStudioAddHelpInContextMenu\":\"True\",\"DFIntHttpCacheCleanMinFilesRequired\":\"10000\",\"DFIntHttpCacheCleanMaxFilesToKeep\":\"7500\",\"FFlagCSGVoxelizer\":\"True\",\"DFFlagCheckApiAccessInTransactionProcessing\":\"True\",\"FFlagBindPurchaseValidateCallbackInMarketplaceService\":\"True\",\"FFlagSetDataModelUniverseIdAfterPublishing\":\"True\",\"FFlagOpenScriptWorksOnModulesEnabled\":\"True\",\"FFlagStudioRibbonBarNewLayout\":\"True\",\"FFlagStudioRibbonBarLayoutFixes\":\"True\",\"FFlagStudioPlaceOnlineIndicator\":\"True\",\"FFlagRenderWangTiles\":\"True\",\"FFlagDisableBadUrl\":\"True\",\"FFlagPrimalSolverLogBarrierIP\":\"True\",\"FFlagDualSolverSimplex\":\"True\",\"FFlagPrimalSolverSimplex\":\"True\",\"FIntNumSmoothingPasses\":\"3\",\"FFlagVerifyConnection\":\"True\",\"FIntRegLambda\":\"1400\",\"FFlagScriptAnalyzerPlaceholder\":\"True\",\"FFlagCSGStripPublishedData\":\"True\",\"DFFlagRaycastReturnSurfaceNormal\":\"True\",\"FFlagMoveGameExplorerActionsIntoContextMenu\":\"True\",\"FFlagStudioAdvanceCookieExpirationBugFixEnabled\":\"True\",\"FFlagNewBackpackScript\":\"True\",\"FFlagNewPlayerListScript\":\"True\",\"FFlagGameNameAtTopOfExplorer\":\"False\",\"FFlagStudioActActionsAsTools\":\"True\",\"FFlagStudioInsertAtMouseClick\":\"True\",\"FFlagStopLoadingStockSounds\":\"True\",\"DFFlagFixTimePositionReplication\":\"True\",\"DFFlagHttpReportStatistics\":\"True\",\"DFFlagEnableChatTestingInStudio\":\"True\",\"DFIntHttpSendStatsEveryXSeconds\":\"300\",\"FLogStepAnimatedJoints\":\"5\",\"DFFlagLuaDisconnectFailingSlots\":\"False\",\"DFFlagEnsureSoundPosIsUpdated\":\"True\",\"DFFlagLoadStarterGearEarlier\":\"False\",\"DFFlagBlockUsersInLuaChat\":\"True\",\"FFlagRibbonPartInsertNotAllowedInModel\":\"True\",\"DFFlagUsePlayerScripts\":\"True\",\"DFFlagUserAccessUserSettings\":\"True\",\"DFFlagUseLuaCameraAndControl\":\"True\",\"DFFlagUseLuaPCInput\":\"True\",\"DFFlagFixLuaMoveDirection\":\"True\",\"DFFlagUseDecalLocalTransparencyModifier\":\"True\",\"DFFlagUseFolder\":\"True\",\"DFFlagUsePreferredSpawnInPlaySoloTeleport\":\"True\",\"DFFlagFilterAddSelectionToSameDataModel\":\"False\",\"FFlagGameExplorerAutofillImageNameFromFileName\":\"True\",\"FFlagGameExplorerBulkImageUpload\":\"True\",\"FFlagStudioAllowAudioSettings\":\"True\",\"DFFlagUsePlayerInGroupLuaChat\":\"True\",\"FFlagStudioDecalPasteFix\":\"True\",\"FFlagStudioCtrlTabDocSwitchEnabled\":\"True\",\"DFIntDraggerMaxMovePercent\":\"60\",\"FFlagUseUniverseGetInfoCallToDetermineUniverseAccess\":\"True\",\"FFlagMaxFriendsCount\":\"True\",\"DFIntPercentApiRequestsRecordGoogleAnalytics\":\"0\",\"FFlagSelectSpinlock\":\"True\",\"FFlagFastZlibPath\":\"True\",\"DFFlagWriteXmlCDataEnabled\":\"True\",\"DFFlagUseSpawnPointOrientation\":\"True\",\"DFFlagUsePlayerSpawnPoint\":\"True\",\"DFFlagCSGPhysicsRecalculateBadContactsInConnectors\":\"True\",\"FFlagStudioPartAlignmentChangeEnabled\":\"True\",\"FFlagStudioToolBoxModelDragFix\":\"True\",\"DFFlagOrder66\":\"False\",\"FFlagCloudIconFixEnabled\":\"True\",\"DFFlagFixHealthReplication\":\"True\",\"DFFlagReplicateAnimationSpeed\":\"True\",\"FFlagLuaFollowers\":\"True\",\"FFlagNewNotificationsScript\":\"True\",\"FFlagStudioSendMouseIdleToPluginMouse\":\"True\",\"DFFlagPhysicsOptimizeAssemblyHistory\":\"True\",\"DFFlagPhysicsOptimizeBallBallContact\":\"True\",\"DFFlagUseNewBubbleSkin\":\"True\",\"DFFlagUse9FrameBackgroundTransparency\":\"True\",\"DFFlagCheckForHeadHit\":\"False\",\"DFFlagUseHttpsForAllCalls\":\"True\",\"DFFlagLoadCoreModules\":\"True\",\"FFlagStudioRecentSavesEnabled\":\"True\",\"FFlagStudioToolBoxInsertUseRayTrace\":\"True\",\"FFlagInterpolationUseWightedDelay\":\"True\",\"FFlagUseInGameTopBar\":\"True\",\"FFlagNewPurchaseScript\":\"True\",\"FFlagStudioEnableGamepadSupport\":\"True\",\"FFlagStudioRemoveDuplicateParts\":\"True\",\"FFlagStudioLaunchDecalToolAfterDrag\":\"True\",\"DFFlagHumanoidFloorPVUpdateSignal\":\"True\",\"DFFlagHumanoidFloorDetectTeleport\":\"True\",\"DFFlagHumanoidFloorForceBufferZone\":\"False\",\"DFFlagHumanoidFloorManualDeltaUpdateManagment\":\"True\",\"DFFlagHumanoidFloorManualFrictionLimitation\":\"True\",\"DFFlagUpdateHumanoidNameAndHealth\":\"True\",\"DFFlagEnableHumanoidDisplayDistances\":\"True\",\"FFlagFixTouchInputEventStates\":\"False\",\"DFFlagInterpolationTimingFix\":\"True\",\"FIntRenderGBufferMinQLvl\":\"16\",\"FFlagResizeGuiOnStep\":\"True\",\"FFlagDontFireFakeMouseEventsOnUIS\":\"True\",\"FFlagCameraUseOwnViewport\":\"True\",\"FFlagGameExplorerMoveImagesUnderAssetsGroup\":\"True\",\"DFFlagNetworkFilterAllowToolWelds\":\"True\",\"DFIntHttpInfluxHundredthsPercentage\":\"5\",\"DFStringHttpInfluxURL\":\"http://influx.calvyy.xyz\",\"DFStringHttpInfluxDatabase\":\"main\",\"DFStringHttpInfluxUser\":\"user\",\"DFStringHttpInfluxPassword\":\"password\",\"FFlagStudioSpawnLocationsDefaultValues\":\"True\",\"FFlagStudioDE11536FixEnabled\":\"True\",\"FFlagStudioRibbonGroupResizeFixEnabled\":\"True\",\"FFlagGradientStep\":\"True\",\"FFlagUseNewContentProvider\":\"False\",\"SFFlagEquipToolOnClient\":\"True\",\"FFlagStartWindowMaximizedDefault\":\"True\",\"FFlagUseNewKeyboardHandling\":\"True\",\"FFlagCameraZoomNoModifier\":\"True\",\"DFFlagRemoteValidateSubscribersError\":\"True\",\"FFlagNewMenuSettingsScript\":\"True\",\"DFFlagHttpCurlSanitizeUrl\":\"True\",\"DFFlagRemoveDataModelDependenceInWaitForChild\":\"True\",\"FFlagFilterAddSelectionToSameDataModel\":\"True\",\"DFFlagUseCanManageApiToDetermineConsoleAccess\":\"True\",\"DFIntMoveInGameChatToTopPlaceId\":\"1\",\"FFlagStudioProgressIndicatorForInsertEnabled\":\"True\",\"FFlagTerrainLazyGrid\":\"True\",\"FFlagHintsRenderInUserGuiRect\":\"True\",\"FFlagCallSetFocusFromCorrectThread\":\"True\",\"FFlagFastRevert\":\"True\",\"FFlagSleepBeforeSpinlock\":\"True\",\"FFlagSparseCheckFastFail\":\"True\",\"FFlagStudioSmoothTerrainPlugin\":\"True\",\"FFlagStudioLoadPluginsLate\":\"True\",\"FFlagStudioInsertIntoStarterPack\":\"True\",\"FFlagStudioIgnoreSSLErrors\":\"True\",\"DFFlagFixJointReparentingDE11763\":\"True\",\"DFFlagPhysicsInvalidateContactCache\":\"True\",\"FFlagLuaMathNoise\":\"True\",\"FFlagArcHandlesBidirectional\":\"True\",\"FFlagChangeHistoryFixPendingChanges\":\"True\",\"DFFlagWorkspaceSkipTerrainRaycastForSurfaceGui\":\"True\",\"FFlagStudioBatchItemMapAddChild\":\"True\",\"FFlagRenderCameraFocusFix\":\"True\",\"DFFlagReplicatorWorkspaceProperties\":\"True\",\"FFlagDirectX11Enable\":\"True\",\"FFlagCheckDegenerateCases\":\"True\",\"DFFlagUseServerCoreScripts\":\"True\",\"DFFlagCorrectFloorNormal\":\"True\",\"FFlagNewBadgeServiceUrlEnabled\":\"True\",\"FFlagBubbleChatbarDocksAtTop\":\"True\",\"FFlagSmoothTerrainClient\":\"True\",\"FFlagLuaUseBuiltinEqForEnum\":\"True\",\"FFlagPlaceLauncherThreadCheckDmClosed\":\"True\",\"DFFlagAppendTrackerIdToTeleportUrl\":\"True\",\"FFlagPlayerMouseRespectGuiOffset\":\"True\",\"DFFlagReportElevatedPhysicsFPSToGA\":\"True\",\"DFFlagPreventReturnOfElevatedPhysicsFPS\":\"True\",\"FFlagStudioIgnoreMouseMoveOnIdle\":\"True\",\"FFlagStudioDraggerFixes\":\"True\",\"FLogUseLuaMemoryPool\":\"0\",\"FFlagCSGNewTriangulate\":\"True\",\"DFFlagLuaFixResumeWaiting\":\"True\",\"FFlagFRMInStudio\":\"True\",\"DFFlagFixLadderClimbSpeed\":\"True\",\"DFFlagNoWalkAnimWeld\":\"False\",\"DFFlagImprovedKick\":\"True\",\"FFlagRenderFixLightGridDirty\":\"True\",\"FFlagLoadLinkedScriptsOnDataModelLoad\":\"True\",\"FFlagFixMeshOffset\":\"True\",\"FIntLaunchInfluxHundredthsPercentage\":\"0\",\"DFIntJoinInfluxHundredthsPercentage\":\"100\",\"FFlagSmoothTerrain\":\"True\",\"FFlagNewVehicleHud\":\"True\",\"DFFlagHumanoidStandOnSeatDestroyed\":\"True\",\"DFFlagGuiBase3dReplicateColor3WithBrickColor\":\"True\",\"FFlagTaskSchedulerCyclicExecutiveStudio\":\"True\",\"DFIntElevatedPhysicsFPSReportThresholdTenths\":\"585\",\"DFIntExpireMarketPlaceServiceCacheSeconds\":\"60\",\"DFFlagEnableMarketPlaceServiceCaching\":\"True\",\"DFFlagUseNewAnalyticsApi\":\"True\",\"DFFlagSmoothTerrainDebounceUpdates\":\"True\",\"FFlagStudioAuthenticationCleanup\":\"True\",\"FFlagRenderFixGBufferLOD\":\"True\",\"FFlagStudioDraggerCrashFixEnabled\":\"True\",\"FFlagDraggerCrashFixEnabled\":\"True\",\"DFFlagEnableRapidJSONParser\":\"True\",\"DFFlagPushLuaWorldRayOriginToNearClipPlane\":\"True\",\"FFlagLoadTimeModificationTestFlag\":\"True\",\"DFFlagPhysicsFastSmoothTerrainUpdate\":\"True\",\"DFFlagSmoothTerrainPhysicsExpandPrimitiveOptimal\":\"True\",\"DFFlagFixBytesOnJoinReporting\":\"True\",\"FFlagRenderGBufferEverywhere\":\"False\",\"DFFlagSmoothTerrainPhysicsRayAabbExact\":\"True\",\"DFIntSmoothTerrainPhysicsRayAabbSlop\":\"1\",\"DFIntMaxClusterKBPerSecond\":\"300\",\"FLogLuaAssert\":\"0\",\"FFlagSmoothTerrainCountCellVolume\":\"True\",\"DFFlagSmoothTerrainWorldToCellUseDiagonals\":\"True\",\"DFFlagFireSelectionChangeOncePerChange\":\"True\",\"FIntLuaAssertCrash\":\"0\",\"FFlagRenderFixCameraFocus\":\"False\",\"DFFlagCSGPhysicsSphereRotationIdentity\":\"True\",\"DFFlagCSGPhysicsRefreshContactsManually\":\"True\",\"FFlagStudioUndoEnabledForEdit\":\"True\",\"DFIntLuaChatFloodCheckMessages\":\"7\",\"DFIntLuaChatFloodCheckInterval\":\"15\",\"FFlagLuaChatFiltering\":\"True\",\"FFlagMobileToggleChatVisibleIcon\":\"True\",\"FFlagStudioDE9132FixEnabled\":\"True\",\"DFFlagGetUserIdEnabled\":\"True\",\"DFFlagGetUserNameEnabled\":\"True\",\"DFFlagEnableAnimationInformationAccess\":\"True\",\"DFFlagEnableAnimationTrackExtendedAPI\":\"True\",\"FFlagRequestServerStatsV2Enabled\":\"True\",\"FFlagPhysicsPreventGroupDraggerPlacementToMinus400_DE6267\":\"True\",\"FFlagSpecificUserdataTypeErrors\":\"True\",\"DFFlagScrollingFrameDraggingFix\":\"True\",\"FFlagAutodetectCPU\":\"True\",\"DFFlagSetRenderedFrameOnClumpChanged\":\"True\",\"DFFlagDisableTimeoutDuringJoin\":\"True\",\"DFFlagDesiredAltitudeDefaultInf\":\"True\",\"DFFlagRCCDE13316CrashFix\":\"True\",\"DFFlagUseStarterPlayerGA\":\"True\",\"FFlagScrollingFrameMouseUpFix\":\"True\",\"DFFlagDebrisServiceUseDestroy\":\"True\",\"DFFlagAccessUserFeatureSetting\":\"True\",\"DFFlagAllowBindActivate\":\"True\",\"FFlagEnableControllerGuiSelection\":\"True\",\"FFlagUseNewSoftwareMouseRender\":\"True\",\"DFFlagDoNotHoldTagItemsForInitialData\":\"True\",\"FFlagAltSpinlock\":\"True\",\"FFlagSpinlock\":\"True\",\"FFlagGraphicsGLReduceLatency\":\"True\",\"DFFlagMovingHumananoidWakesFloor\":\"True\",\"DFFlagSetNetworkOwnerAPIEnabled\":\"True\",\"DFFlagSetNetworkOwnerAPIEnabledV2\":\"True\",\"DFFlagGetFriendsEnabled\":\"True\",\"DFFlagGetFriendsOnlineEnabled\":\"True\",\"DFFlagUseNewTextBoxLogic\":\"True\",\"FFlagOnScreenProfilerGPU\":\"True\",\"FFlagConfigurableLineThickness\":\"True\",\"DFFlagSpawnPointEnableProperty\":\"True\",\"DFFlagConfigurableFallenPartDestroyHeight\":\"True\",\"DFFlagMiddleMouseButtonEvent\":\"True\",\"DFFlagEnablePreloadAsync\":\"True\",\"DFFlagFoldersInGUIs\":\"True\",\"DFIntAndroidInfluxHundredthsPercentage\":\"0\",\"DFFlagNoOwnershipLogicOnKernelJoint\":\"True\",\"DFFlagEnableGetPlayingAnimationTracks\":\"True\",\"FFlagCyclicExecutivePriorityJobs\":\"True\",\"DFIntMaxMissedWorldStepsRemembered\":\"16\",\"DFIntMacInfluxHundredthsPercentage\":\"0\",\"DFIntiOSInfluxHundredthsPercentage\":\"100\",\"FFlagStudioLockServiceParents\":\"True\",\"DFFlagFirePlayerAddedAndPlayerRemovingOnClient\":\"True\",\"DFFlagRecursiveWakeForBodyMovers\":\"True\",\"DFFlagEnableHumanoidSetStateEnabled\":\"True\",\"DFFlagSoundEndedEnabled\":\"True\",\"DFFlagUseIntersectingOthersForSpawnEnabled\":\"True\",\"DFFlagMoveToDontAlignToGrid\":\"True\",\"DFFlagIsBestFriendsWithReturnFriendsWith\":\"True\",\"DFFlagPlayerOwnsAssetFalseForInvalidUsers\":\"True\",\"DFFlagIsUrlCheckAssetStringLength\":\"True\",\"DFFlagEnableGoodbyeDialog\":\"True\",\"DFFlagEnableReverseAnimations\":\"True\",\"DFFlagEnableAnimationSetTimePosition\":\"True\",\"DFFlagEnableMobileAutoJumpFlag\":\"True\",\"DFFlagHttpDelaySendInfluxStats\":\"True\",\"DFFlagDisableRequestMarker\":\"True\",\"DFFlagDisableCharacterRequest\":\"True\",\"FFlagStudioCollapsibleTutorials\":\"True\",\"FStringStudioTutorialsUrl\":\"http://wiki.roblox.com/index.php?title=Studio_Tutorials_Test&studiomode=true\",\"FStringStudioTutorialsTOCUrl\":\"http://wiki.roblox.com/index.php?title=Studio_Tutorials_Landing&studiomode=true\",\"FFlagSandboxHash\":\"True\",\"FFlagDE14316CrashFix\":\"True\",\"FFlagDE14317CrashFix\":\"True\",\"DFFlagSetNetworkOwnerCanSetCheck\":\"True\",\"DFFlagLiveColorUpdatesCanceling\":\"True\",\"DFFlagLuaGcPerVm\":\"True\",\"FFlagStudioDeviceEmulationTouchInputFix\":\"True\",\"FFlagTaskSchedulerCyclicExecutive\":\"True\",\"DFFlagMakeWebPendingFriendRequests\":\"True\",\"DFFlagGetLastestAssetVersionEnabled\":\"True\",\"FFlagHideDeprecatedEnums\":\"True\",\"FFlagSubmitEditedColor3WhenFocusLost\":\"True\",\"DFFlagFixScriptableCameraRoll\":\"True\",\"DFFlagSeparateBulletNarrowPhaseAndMidStepUpdates\":\"True\",\"DFFlagUsePGSSolverSpringConstantScale\":\"True\",\"DFFlagToolRequiresHandleProperty\":\"True\",\"FFlagPlayerScriptsNotArchivable\":\"True\",\"DFFlagClearAllChildrenUseDestroy\":\"True\",\"DFFlagMaxPlayersEnabled\":\"True\",\"DFFlagPreferredPlayersEnabled\":\"True\",\"DFFlagLocalHumanoidSoundsEnabled\":\"True\",\"DFFlagIncreaseSoundPositionClampLimit\":\"True\",\"DFFlagNameOcculsionIgnoreTransparent\":\"True\",\"DFFlagReconstructAssetUrlNew\":\"True\",\"DFFlagAdjustFloorForce\":\"True\",\"DFFlagFixAnimationPhaseInitialization\":\"True\",\"FFlagLuaChatPhoneFontSize\":\"True\",\"DFFlagUseAssetTypeHeader\":\"True\",\"FFlagCSGUnionCatchUnknownExceptions\":\"False\",\"FIntGamePerfMonitorPercentage\":\"10\",\"FFlagSoundTypeCheck\":\"True\",\"DFFlagIncreaseScrollWheelMultiplyTime\":\"True\",\"FFlagMacRemoveUserInputJob\":\"True\",\"FFlagStudioNewFonts\":\"True\",\"DFFlagApiCapitalizationChanges\":\"True\",\"FFlagParticleCullFix\":\"True\",\"DFFlagVideoCaptureTeleportFix\":\"False\",\"DFFlagCoreGuiCustomizationApi\":\"True\",\"DFFlagCustomTeleportLoadingScreen\":\"True\",\"DFFlagCharacterAppearanceLoadedEnabled\":\"True\",\"DFFlagVIPServerOwnerIdEnabled\":\"True\",\"DFFlagEnableParticleVelocityInheritance\":\"True\",\"DFFlagEnableParticleEmissionDirection\":\"True\",\"DFFlagFixParticleDistribution\":\"True\",\"DFFlagEnableParticleNewBoundingBox\":\"True\",\"DFFlagEnableParticlePartLock\":\"True\",\"DFFlagEnableParticleBurst\":\"True\",\"DFFlagNoRunSteepSlope\":\"True\",\"DFFlagHumanoidJumpPower\":\"True\",\"FFlagControllerMenu\":\"True\",\"FFlagFlyCamOnRenderStep\":\"True\",\"DFFlagFullscreenEnabledWhileRecording\":\"True\",\"DFFlagPreProcessTextBoxEvents\":\"True\",\"DFFlagAllowHideHudShortcut\":\"False\",\"DFFlagFixBallInsideBlockCollision\":\"True\",\"FFlagPGSSolverBodyCacheLeakFix\":\"True\",\"FFlagFixCrashAtShutdown\":\"True\",\"DFFlagEquipClonedToolFix\":\"True\",\"FFlagGamepadCursorChanges\":\"True\",\"DFFlagCreatePlayerGuiLocal\":\"False\",\"DFFlagDontUseInsertServiceOnAnimLoad\":\"True\",\"DFFlagCyclicExecutiveFixNonCyclicJobRun\":\"True\",\"DFFlagPhysicsFPSTimerFix\":\"True\",\"FFlagCyclicExecutiveRenderJobRunsFirst\":\"True\",\"FFlagPhysicsCylinders\":\"True\",\"DFFlagPhysicsUseNewBulletContact\":\"True\",\"FFlagReadCoordinateFrameFast\":\"False\",\"DFFlagRayHitMaterial\":\"True\",\"DFFlagPromptPurchaseOnGuestHandledInCoreScript\":\"True\",\"DFFlagNonEmptyPcallError\":\"True\",\"DFFlagDisplayTextBoxTextWhileTypingMobile\":\"False\",\"DFFlagOverrideScollingDisabledWhenRecalulateNeeded\":\"True\",\"DFFlagFixScrollingOffSurfaceGUIs\":\"True\",\"DFFlagTextScaleDontWrapInWords\":\"True\",\"DFFlagListenPositionEnabled\":\"True\",\"DFFlagBackTabInputInStudio\":\"True\",\"DFFlagTrackLastDownGUI\":\"True\",\"DFFlagBulletFixCacheReuse\":\"True\",\"DFFlagFastFilterHumanoidParts\":\"False\",\"DFFlagProcessAcceleratorsBeforeGUINaviagtion\":\"True\",\"DFFlagImageFailedToLoadContext\":\"True\",\"DFFlagDontReorderScreenGuisWhenDescendantRemoving\":\"True\",\"DFFlagSoundFailedToLoadContext\":\"True\",\"DFFlagAnimationFailedToLoadContext\":\"True\",\"DFFlagElasticEasingUseTwoPi\":\"True\",\"SFFlagNetworkUseServerScope\":\"True\",\"DFFlagHttpZeroLatencyCaching\":\"True\",\"DFFlagPasteWithCapsLockOn\":\"True\",\"DFFlagHttpCurlDomainTrimmingWithBaseURL\":\"False\",\"DFFlagLoadFileUseRegularHttp\":\"True\",\"DFFlagReplicatorCheckBadRebinding\":\"True\",\"FFlagFastClusterThrottleUpdateWaiting\":\"True\",\"DFFlagDeserializePacketThreadEnabled\":\"True\",\"FFlagFontSizeUseLargest\":\"True\",\"DFFlagRejectHashesInLinkedSource\":\"True\",\"FFlagUpdatePrimitiveStateForceSleep\":\"True\",\"FFlagPhysicsUseKDTreeForCSG\":\"True\",\"DFFlagCSGLeftoverDataFix\":\"True\",\"FFlagStudioTutorialSeeAll\":\"True\",\"DFFlagLimitScrollWheelMaxToHalfWindowSize\":\"True\",\"FFlagGameExplorerCopyPath\":\"True\",\"DFFlagFixRotatedHorizontalScrollBar\":\"True\",\"DFFlagFixAnchoredSeatingPosition\":\"True\",\"FFlagFixSlice9Scale\":\"True\",\"DFFlagFullscreenRefocusingFix\":\"True\",\"DFFlagEnableClimbingDirection\":\"True\",\"FFlagPGSGlueJoint\":\"True\",\"FFlagTweenCallbacksDuringRenderStep\":\"True\",\"FFlagFRMFixCullFlicker\":\"True\",\"DFFlagDisableProcessPacketsJobReschedule\":\"True\",\"FFlagCSGVoxelizerPrecompute\":\"False\",\"FFlagLazyRenderingCoordinateFrame\":\"True\",\"FFlagPGSSteppingMotorFix\":\"True\",\"DFFlagLockViolationScriptCrash\":\"False\",\"DFFlagLockViolationInstanceCrash\":\"False\",\"FFlagSpheresAllowedCustom\":\"True\",\"DFFlagHumanoidCookieRecursive\":\"True\",\"FFlagRwxFailReport\":\"True\",\"FIntStudioInsertDeletionCheckTimeMS\":\"30000\",\"DFFlagClampRocketThrustOnPGS\":\"True\",\"DFFlagPGSWakePrimitivesWithBodyMoverPropertyChanges\":\"True\",\"FFlagPGSUsesConstraintBasedBodyMovers\":\"True\",\"FFlagUseNewSubdomainsInCoreScripts\":\"True\",\"DFFlagEnableShowStatsLua\":\"True\",\"FFlagSmoothTerrainPacked\":\"True\",\"DFFlagUrlReconstructToAssetGame\":\"False\",\"FFlagPGSApplyImpulsesAtMidpoints\":\"True\",\"FFlagModifyDefaultMaterialProperties\":\"True\",\"FIntPhysicalPropFriction_SMOOTH_PLASTIC_MATERIAL\":\"200\",\"FIntPhysicalPropFriction_PLASTIC_MATERIAL\":\"300\",\"FIntPhysicalPropFriction_NEON_MATERIAL\":\"300\",\"FIntPhysicalPropFriction_SNOW_MATERIAL\":\"300\",\"FIntPhysicalPropFriction_ALUMINUM_MATERIAL\":\"400\",\"FIntPhysicalPropFriction_BRICK_MATERIAL\":\"800\",\"FIntPhysicalPropFriction_CONCRETE_MATERIAL\":\"700\",\"FIntPhysicalPropFriction_DIAMONDPLATE_MATERIAL\":\"350\",\"FIntPhysicalPropFriction_SANDSTONE_MATERIAL\":\"500\",\"FIntPhysicalPropFriction_SAND_MATERIAL\":\"500\",\"FIntPhysicalPropFWeight_ICE_MATERIAL\":\"3000\",\"FIntPhysicalPropFWeight_BRICK_MATERIAL\":\"300\",\"FIntPhysicalPropFWeight_CONCRETE_MATERIAL\":\"300\",\"FIntPhysicalPropFWeight_SANDSTONE_MATERIAL\":\"5000\",\"FIntPhysicalPropFWeight_BASALT_MATERIAL\":\"300\",\"FIntPhysicalPropFWeight_SAND_MATERIAL\":\"5000\",\"FIntPhysicalPropElasticity_SAND_MATERIAL\":\"50\",\"FIntPhysicalPropElasticity_SNOW_MATERIAL\":\"30\",\"FIntPhysicalPropElasticity_MUD_MATERIAL\":\"70\",\"FIntPhysicalPropElasticity_GROUND_MATERIAL\":\"100\",\"FIntPhysicalPropElasticity_MARBLE_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_BRICK_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_PEBBLE_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_COBBLESTONE_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_ROCK_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_SANDSTONE_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_BASALT_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_CRACKED_LAVA_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_FABRIC_MATERIAL\":\"50\",\"FIntPhysicalPropElasticity_WOOD_MATERIAL\":\"200\",\"FIntPhysicalPropElasticity_WOODPLANKS_MATERIAL\":\"200\",\"FIntPhysicalPropElasticity_ICE_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_GLACIER_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_RUST_MATERIAL\":\"200\",\"FIntPhysicalPropElasticity_DIAMONDPLATE_MATERIAL\":\"250\",\"FIntPhysicalPropElasticity_ALUMINUM_MATERIAL\":\"250\",\"FIntPhysicalPropElasticity_METAL_MATERIAL\":\"250\",\"FIntPhysicalPropElasticity_GRASS_MATERIAL\":\"100\",\"DFFlagFixSeatingWhileSitting\":\"True\",\"FFlagPGSSolverDefaultOnNewPlaces\":\"True\",\"FFlagPGSVariablePenetrationMargin\":\"False\",\"FIntPGSPentrationMarginMax\":\"50000\",\"FFlagStudioHideMouseCoursorOnCommand\":\"True\",\"SFFlagNewPhysicalPropertiesForcedOnAll\":\"True\",\"SFFlagMaterialPropertiesNewIsDefault\":\"True\",\"DFFlagMaterialPropertiesEnabled\":\"True\",\"FFlagWaterParams\":\"True\",\"FFlagSpatialHashMoreRoots\":\"True\",\"FFlagSkipAdornIfWorldIsNull\":\"True\",\"DFStringWorkspaceMessageLink\":\"http://devforum.roblox.com/t/improving-the-safety-of-our-community/33201\",\"DFStringWorkspaceMessageText\":\"Clickheretoreadanimportantmessageaboutimprovingthesafetyofourcommunity\",\"DFIntStudioWorkspaceNotificationLevel\":\"0\",\"DFFlagNetworkOwnershipRuleReplicates\":\"True\",\"DFFlagSmoothTerrainWriteVoxelsOccupancyFix\":\"True\",\"DFFlagCloudEditByPassCheckForServer\":\"True\",\"DFFlagDraggerUsesNewPartOnDuplicate\":\"True\",\"DFFlagRestoreTransparencyOnToolChange\":\"False\",\"FFlagEnableLuaFollowers\":\"False\",\"DFFlagUserServerFollowers\":\"True\",\"FFlagNetworkReplicateTerrainProperties\":\"True\",\"FFlagAllowInsertFreeModels\":\"False\",\"FFlagInsertUnderFolder\":\"True\",\"DFFlagPGSWakeOtherAssemblyForJoints\":\"True\",\"FFlagStudioPropertySliderEnabled\":\"True\",\"DFFlagSetNetworkOwnerFixAnchoring\":\"True\",\"FFlagFixBulletGJKOptimization\":\"True\",\"FFlagOSXUseSDL\":\"False\",\"DFFlagPhysicalPropMassCalcOnJoin\":\"False\",\"DFFlagBrickColorParseNonDeprecatedMatchEnabled\":\"True\",\"FFlagWindowsUseSDL\":\"False\",\"FFlagPhysicsOptimizeSendClumpChanged\":\"True\",\"DFFlagHumanoidFeetIsPlastic\":\"True\",\"DFFlagUseTerrainCustomPhysicalProperties\":\"True\",\"DFFlagFormFactorDeprecated\":\"True\",\"FFlagPGSVariablePenetrationMarginFix\":\"True\",\"DFIntDataStoreMaxValueSize\":\"262144\",\"DFFlagFixShapeChangeBug\":\"True\",\"FFlagScriptAnalyzerFixLocalScope\":\"True\",\"FFlagRenderVRBBGUI\":\"True\",\"FFlagRenderVR\":\"True\",\"DFFlagNetworkFixJoinDataItemOrder\":\"True\",\"FFlagStudioImproveModelDragFidelity\":\"True\",\"FFlagStudioOrthonormalizeSafeRotate\":\"True\",\"FFlagMacInferredCrashReporting\":\"True\",\"FFlagWindowsInferredCrashReporting\":\"True\",\"FFlagCloudEditDoNotLoadCoreScripts\":\"True\",\"FFlagStudioEmbeddedFindDialogEnabled\":\"True\",\"FFlagUserAllCamerasInLua\":\"False\",\"DFFlagMacInferredCrashReporting\":\"True\",\"DFFlagWindowsInferredCrashReporting\":\"True\",\"FFlagUseNewPromptEndHandling\":\"True\",\"FFlagPhysPropConstructFromMaterial\":\"True\",\"FFlagStudioToolboxModelDragToCastPoint\":\"True\",\"FFlagStudioPushTreeWidgetUpdatesToMainThread\":\"True\",\"DFFlagFixYieldThrottling\":\"True\",\"FFlagCheckSleepOptimization\":\"True\",\"DFFlagContactManagerOptimizedQueryExtents\":\"True\",\"FFlagUseBuildGenericGameUrl\":\"True\",\"DFFlagFixFallenPartsNotDeleted\":\"True\",\"DFFlagTrackPhysicalPropertiesGA\":\"True\",\"DFFlagSetNetworkOwnerFixAnchoring2\":\"True\",\"FFlagUseSimpleRapidJson\":\"True\",\"DFFlagTurnOffFakeEventsForCAS\":\"True\",\"DFFlagTurnOffFakeEventsForInputEvents\":\"True\",\"FFlagCancelPendingTextureLoads\":\"False\",\"DFFlagCachedPoseInitialized\":\"True\",\"DFFlagFixJumpGracePeriod\":\"True\",\"FFlagFilterSinglePass\":\"True\",\"DFFlagOrthonormalizeJointCoords\":\"True\",\"DFFlagPhysicsSenderUseOwnerTimestamp\":\"False\",\"DFFlagNamesOccludedAsDefault\":\"True\",\"FFlagUserUseNewControlScript\":\"True\",\"FFlagUseDynamicTypesetterUTF8\":\"True\",\"DFFlagUseNewPersistenceSubdomain\":\"True\",\"DFFlagUseNewDataStoreLogging\":\"True\",\"FFlagPlaceLauncherUsePOST\":\"True\",\"FFlagStudioUpdateSAResultsInUIThread\":\"True\",\"FFlagBillboardGuiVR\":\"True\",\"FFlagHumanoidRenderBillboard\":\"True\",\"FLogVR\":\"6\",\"FFlagStudioRemoveDebuggerResumeLock\":\"True\",\"FFlagAnalyzerGroupUIEnabled\":\"True\",\"DFFlagVariableHeartbeat\":\"True\",\"DFFlagScreenShotDuplicationFix\":\"True\",\"FFlagCSGDelayParentingOperationToEnd\":\"True\",\"FFlagStudioTreeWidgetCheckDeletingFlagWhenDoingUpdates\":\"True\",\"DFFlagUseComSiftUpdatedWebChatFilterParamsAndHeader\":\"False\",\"DFFlagConstructModerationFilterTextParamsAndHeadersUseLegacyFilterParams\":\"False\",\"FFlagMinMaxDistanceEnabled\":\"True\",\"FFlagRollOffModeEnabled\":\"True\",\"DFFlagGetLocalTeleportData\":\"True\",\"FFlagUseNewXboxLoginFlow\":\"True\",\"DFFlagFixSlowLadderClimb\":\"True\",\"DFFlagHumanoidCheckForNegatives\":\"True\",\"DFFlagFixMatrixToAxisAngle\":\"True\",\"DFFlagMaskWeightCleared\":\"True\",\"DFFlagUseStarterPlayerCharacterScripts\":\"True\",\"DFFlagUseStarterPlayerHumanoid\":\"True\",\"DFFlagAccessoriesAndAttachments\":\"True\",\"FFlagTeamCreateOptimizeRemoteSelection\":\"True\",\"FFlagReportInGameAssetSales\":\"True\",\"FFlagFilterDoublePass\":\"False\",\"DFFlagRaiseSendPriority\":\"False\",\"FFlagUsePreferredSoundDevice\":\"True\",\"FFlagRenderLowLatencyLoop\":\"False\",\"DFFlagLocalScriptSpawnPartAlwaysSetOwner\":\"True\",\"DFFlagCloudEditSupportImmediatePublish\":\"True\",\"FFlagFixSurfaceGuiGamepadNav\":\"True\",\"DFFlagEnableAdColony\":\"False\",\"FFlagEnableAdServiceVideoAds\":\"False\",\"DFFlagInfluxDb09Enabled\":\"True\",\"DFFlagTeleportSignalConnectOnServiceProvider\":\"True\",\"DFFlagScriptContextGuardAgainstCStackOverflow\":\"True\",\"FFlagFixPhysicalPropertiesComponentSet\":\"True\",\"DFFlagMaterialPropertiesDivideByZeroWeights\":\"True\",\"FFlagRemoveUnusedPhysicsSenders\":\"True\",\"FFlagRemoveInterpolationReciever\":\"True\",\"DFFlagActivatePGSMechanicalJoints\":\"True\",\"FIntPhysicalPropDensity_ALUMINUM_MATERIAL\":\"2700\",\"FFlagTreatCloudEditAsEditGameMode\":\"True\",\"FFlagSendFilteredExceptionOnInferredStep\":\"True\",\"DFFlagUrlReconstructToAssetGameSecure\":\"False\",\"DFFlagUseModerationFilterTextV2\":\"True\",\"FFlagGraphicsD3D10\":\"True\",\"FFlagRenderFixFog\":\"True\",\"FFlagUseNewAppBridgeWindows\":\"True\",\"DFFlagNullCheckJointStepWithNullPrim\":\"True\",\"FFlagJNIEnvScopeOptimization\":\"True\",\"DFFlagSanitizeLoadingGUICorrected\":\"True\",\"FFlagSendLegacyMachineConfigInfo\":\"False\",\"FFlagUseNewBadgesCreatePage\":\"True\",\"FFlagRetryWhenCloudEditEnabledEndpointFails\":\"True\",\"DFFlagTeamCreateDoNotReplicateShowDevGuiProp\":\"True\",\"FFlagStudioAddBackoffToNotificationsReconnects\":\"True\",\"DFFlagInsertServiceForceLocalInTeamCreate\":\"True\",\"FFlagGraphicsMacFix\":\"True\",\"FFlagUseNewAppBridgeOSX\":\"True\",\"FFlagNewColor3Functions\":\"True\",\"DFFlagSmootherVehicleSeatControlSystem\":\"True\",\"FFlagGameExplorerUseV2AliasEndpoint\":\"True\",\"FFlagDisableAbortRender\":\"True\",\"DFFlagInstancePredeleteNuke\":\"True\",\"DFFlagSimpleHermiteSplineInterpolate\":\"False\",\"DFFlagCleanUpInterpolationTimestamps\":\"True\",\"SFFlagPhysicsPacketSendWorldStepTimestamp\":\"True\",\"DFFlagUpdateTimeOnDelayedSamples\":\"False\",\"DFFlagDisableMovementHistory\":\"True\",\"DFFlagLookForDuplicateCoordinateFrameInBuffer\":\"True\",\"DFFlagDoNotForwardClientTimestamp\":\"True\",\"DFFlagZeroVelocityOnDelayedSamples\":\"True\",\"DFFlagUpdateHermiteLastFrameWhenUpdatePrevFrame\":\"True\",\"DFFlagCatchThrottledVelocityComponents\":\"True\",\"DFIntThrottledVelocityThresholdTenths\":\"15\",\"DFFlagShowFormFactorDeprecatedWarning\":\"False\",\"FFlagStudioTeamCreateWebChatBackendEnabled\":\"True\",\"DFFlagAnimationEasingStylesEnabled\":\"True\",\"FFlagUseVRKeyboardInLua\":\"True\",\"DFFlagCheckDataModelOnTeleportRetry\":\"True\",\"DFStringHttpInfluxWriterPassword\":\"faster1Play\",\"DFFlagOptimizeAnimator\":\"True\",\"FFlagOptimizeAnimatorCalcJoints\":\"True\",\"DFFlagStopUsingMaskWeight\":\"True\",\"FFlagRenderNoDepthLast\":\"True\",\"DFFlagFixTimeStampsForRunningNoThrottle\":\"True\",\"DFIntInterpolationDelayFactorTenths\":\"10\",\"DFFlagUseHermiteSplineInterpolation\":\"True\",\"DFFlagChatServiceFilterStringForPlayerFromAndToStudioBypass\":\"True\",\"FFlagCameraInterpolateMethodEnhancement\":\"False\",\"DFFlagBlendPosesWithIsOver\":\"True\",\"FFlagRestrictSales\":\"True\",\"FFlagBadTypeOnPcallEnabled\":\"True\",\"FFlagFixMouseFireOnEmulatingTouch\":\"True\",\"FFlagUseUpdatedSyntaxHighlighting\":\"True\",\"FFlagFixStickyDragBelowOrigin\":\"True\",\"FFlagFixBadMemoryOnStreamingGarbageCollection\":\"True\",\"DFFlagFixAllCharacterStreaming\":\"True\",\"FFlagDisableChangedServiceInTestMode\":\"True\",\"FFlagAllowFullColorSequences\":\"True\",\"FFlagStudioAllowFullColorSequences\":\"True\",\"DFFlagDynamicGravity\":\"True\",\"FFlagUseNewAppBridgeAndroid\":\"True\",\"FFlagFixSurfaceGuiGazeSelect\":\"True\",\"FFlagFixAlwaysOnTopSurfaceGuiInput\":\"True\",\"DFFlagCSGPreventCrashesWhenPartOperationsNotInDataModel\":\"True\",\"DFFlagUsePointsNewBatchingImpl\":\"True\",\"FFlagUseUpdatedKeyboardSettings\":\"False\",\"DFFlagFixAnimationControllerAnimator\":\"True\",\"DFFlagNoAnimationTrackState\":\"True\",\"DFFlagFixNestedAnimators\":\"True\",\"DFFlagWaitForToolHandleToEquip\":\"True\",\"DFFlagUseNewFetchFriendsFunction\":\"True\",\"FFlagWindowsNoDmpRetry\":\"False\",\"FFlagDeleteLogsOnMac\":\"True\",\"FFlagDeleteLogsByDate\":\"True\",\"FFlagTestMenuEnabledOnAllWindows\":\"True\",\"FFlagSoundServiceGameConfigurerConfigureRunServiceRun\":\"True\",\"DFFlagDoUpdateStepDetachedChannels\":\"True\",\"FFlagSoundChannelMaxDistanceStopFMODChannel\":\"True\",\"FFlagRenderSoftParticles\":\"True\",\"FFlagScriptContextSinglePendingThreadsQueue\":\"False\",\"DFIntTeleportExceptionInfluxHundredthsPercentage\":\"9000\",\"FIntStartupInfluxHundredthsPercentage\":\"100\",\"FFlagCSGAllowUnorderedProperties\":\"False\",\"DFFlagGamepadProcessMouseEvents\":\"False\",\"DFFlagCrashTouchTransmitterIfRefDtor\":\"False\",\"FFlagRenderUserGuiIn3DSpace\":\"True\",\"FFlagScreenGuisClipDescendants\":\"True\",\"FFlagUseNewNotificationPathLua\":\"True\",\"FFlagVideoDocumentationPluginEnabled\":\"True\",\"FFlagStudioBreakOnInfiniteLoops\":\"True\",\"FFlagMessageOnLoadScriptValidationFail\":\"True\",\"FFlagStudioMockPurchasesEnabled\":\"True\",\"FFlagStudioUseMarketplaceApiClient\":\"True\",\"DFFlagUseGameAvatarTypeEnum\":\"False\",\"FFlagStudioUsePlaySoloConfigurer\":\"True\",\"DFFlagUseAvatarFetchAPI\":\"False\",\"DFFlagSetHumanoidRegardlessOfNetworkOwnership\":\"True\",\"FFlagFixStudioCursorJitter\":\"True\",\"FFlagVoxelCompressedStorage\":\"True\",\"FFlagSmoothTerrainLODEnabled\":\"True\",\"FFlagBetterTabManagement\":\"True\",\"DFFlagBlockCustomHttpHeaders\":\"False\",\"FFlagStudioInsertAtTopCenterOfSelection\":\"True\",\"DFFlagCloudEditRemoveEditorOnPlayerRemove\":\"True\",\"FFlagWaitForChildTimeOut\":\"True\",\"FFlagDeviceEmulationStatistics\":\"True\",\"FFlagFixBoxSelectWithCtrl\":\"True\",\"FFlagStudioTrimPropertyWhitespace\":\"True\",\"FFlagDebugCSGExportFailure\":\"False\",\"FFlagFixCrashOnEmptyTextOnAutoComplete\":\"True\",\"FFlagCancelInputOnGuiNavigation\":\"True\",\"FFlagRemoveOldAnalyticsImplementation\":\"True\",\"FFlagRemoveOldCountersImplementation\":\"True\",\"FFlagUseNewAppBridgeStudio\":\"True\",\"FFlagStudioAnalyticsRefactoring\":\"True\",\"DFFlagRCCUseMarketplaceApiClient\":\"False\",\"FFlagStudioIntellesenseOnAllMembersEnabled\":\"True\",\"DFFlagDataStoreDisableReFetchingRecentKeys\":\"True\",\"FFlagNewDefaultScriptSource\":\"True\",\"FFlagStudioEnableDebuggerPerfImprovements\":\"True\",\"FFlagRecordForceStereo\":\"True\",\"FFlagStudioVideoRecordFix\":\"True\",\"FFlagStudioUseHttpsForUserInfo\":\"True\",\"FFlagUseHttpsForGameserverAshx\":\"True\",\"FFlagDisableScriptContextScriptsDisabled\":\"True\",\"DFFlagDuplicateInstanceReferenceFix\":\"True\",\"FFlagRakNetSupportIpV6\":\"False\",\"FFlagUseToStringN\":\"True\",\"FFlagStudioRenderRemotePlayerSelection\":\"True\",\"FFlagStackTraceLinks\":\"True\",\"FFlagStudioUpdateRestoreBehavior\":\"True\",\"FFlagTouchTransmitterWeakPtr\":\"True\",\"FFlagAdvancedRCCLoadFMODRetry\":\"True\",\"FFlagAdvancedRCCLoadFMODReportDeviceInfo\":\"True\",\"FFlagAdvancedRCCLoadFMODAttemptReportDeviceInfoOnFailure\":\"True\",\"FFlagClientLoadFMODReportDeviceInfo\":\"True\",\"DFIntReportDeviceInfoRate\":\"100\",\"DFFlagSoundV2LogOnSetSoundId\":\"True\",\"FFlagMouseUseUserInputServiceMouse\":\"True\",\"SFFlagSoundChannelUseV2Implementation\":\"True\",\"SFFlagUseNewSetFMOD3D\":\"True\",\"FFlagCSGReportSuccessFailure\":\"True\",\"FFlagUseAvatarFetchThumbnailLogic\":\"True\",\"FFlagDoIncrementalLoadingForR6AvatarFetch\":\"True\",\"FFlagUseAvatarFetchAPI\":\"True\",\"FFlagUseGameAvatarTypeEnum\":\"True\",\"FFlagSmoothTerrainLODFalseCoarseNeighbor\":\"True\",\"FFlagStudioPublishToRobloxActionUXAlwaysAvailable\":\"True\",\"FFlagFixArraysNotUnmarkedFromCyclicTableDetection\":\"True\",\"FFlagSoundIgnoreReplicatorJoinDataItemCache\":\"True\",\"FFlagStudioReportCachedRecentActions\":\"True\",\"FFlagStudioCacheRecentAction\":\"True\",\"FFlagRenderMeshReturnsCorrectly\":\"False\",\"FFlagEnableRenderCSGTrianglesDebug\":\"False\",\"FFlagStudioBreakOnInfiniteLoopsThreadingFixEnabled\":\"True\",\"SFFlagNetworkStreamRotationAsFloat\":\"False\",\"DFFlagStarterGuiMethodsWarnServer\":\"True\",\"DFFlagStarterGuiPropertiesReplicate\":\"True\",\"DFFlagClickDetectorReplicate\":\"True\",\"FFlagTeleportDetailedInfluxHttpStatusError\":\"True\",\"DFFlagHttpStatusCodeErrorIncludeBody\":\"True\",\"FFlagEnableVoiceASR\":\"False\",\"DFFlagFMODSetAccurateTime\":\"True\",\"FFlagRenderFixGuiOrder\":\"True\",\"FFlagExplosionsVisiblePropertyEnabled\":\"True\",\"FFlagEnableVoiceRecording\":\"False\",\"FFlagStudioReopenClosedTabsShortcut\":\"True\",\"FFlagStudioShowNotSavingScriptEditsOnce\":\"True\",\"FFlagHttpCurlCacheHandles\":\"True\",\"DFFlagAvatarFetchCanLoadCharacterAppearanceFix\":\"True\",\"FFlagGraphicsTextureCommitChanges\":\"False\",\"DFFlagLoadInstancesAsyncUseDataModelTasks\":\"False\",\"DFFlagInfluxOverUDP\":\"True\",\"FFlagUseLegacyEnumItemLookup\":\"True\",\"DFFlagInfluxOverTCP\":\"False\",\"DFFlagCFrameRightAndUpVectors\":\"True\",\"DFFlagLegacyBodyColorsOnCharacterLoadFailure\":\"True\",\"DFFlagUseMeshPartR15\":\"True\",\"FFlagStudioFixLockingScriptDisablesMenuOptions\":\"True\",\"FFlagUseCorrectDoppler\":\"True\",\"DFFlagFixJumpRequestOnLand\":\"True\",\"DFFlagNullStarterCharacterPrimaryPartFix\":\"True\",\"SFFlagR15CompositingEnabled\":\"True\",\"FFlagStudioFixPropertiesWindowScrollBarNotShowing\":\"True\",\"FFlagFixCollisionFidelityTeamCreate\":\"True\",\"DFFlagSendHttpBodyOnFailure\":\"True\",\"DFFlagCSGPreventNoContextCSGCrashes\":\"False\",\"FFlagConstraintPropertyReplicationRaceConditionFixEnabled\":\"True\",\"FFlagFixLogCulling\":\"True\",\"FFlagSmoothTerrainLODFixSeams\":\"True\",\"DFFlagDontCacheHumanoids\":\"True\",\"FFlagStudioPlaySoloConfigurerLegacyPlayerName\":\"True\",\"DFFlagPartForRegion3NoMaxLimit\":\"True\",\"FFlagRCCSupportTeamTest\":\"True\",\"FFlagStudioSupportBytecodeDeserialize\":\"True\",\"DFFlagStackTraceHasNewLines\":\"True\",\"DFFlagCleanCacheMoveMutex\":\"True\",\"FFlagFastFontMeasure\":\"False\",\"FFlagRenderMoreFonts\":\"True\",\"FFlagCleanFilteringEnabledLocalSpawnParts\":\"True\",\"FFlagFetchJoinScriptWithHttp\":\"False\",\"FFlagCheckPlayerProcessMutexCreation\":\"True\",\"FFlagCheckRegisterSoundChannelUniqueness\":\"True\",\"FFlagUseCommonModules\":\"True\",\"DFFlagRemoteFixDisconnectedPlayer\":\"True\",\"FFlagWarnForLegacyTerrain\":\"True\",\"FFlagFastClusterDisableReuse\":\"True\",\"FFlagStudioRespectMeshOffset\":\"False\",\"DFFlagRevisedClientJoinMetrics\":\"True\",\"FFlagRestoreScriptSourceWhenRedoingScriptCreation\":\"True\",\"DFFlagStudioFixPastingDecalsIntoMultiple\":\"True\",\"FFlagMeshPartMaterialTextureSupport\":\"True\",\"FFlagStudioPlaySoloCharacterAutoLoadsNullTool\":\"True\",\"DFFlagPGSWakeOtherIfOneAssemblyIsAwake\":\"False\",\"DFFlagFixR15BodyPhysics\":\"True\",\"FFlagLoadCharacterSoundFromCorescriptsRepo\":\"True\",\"DFFlagSoundV2LoadUseParamContext\":\"True\",\"DFFlagSoundV2LoadedRunCallbacks\":\"True\",\"FFlagFixPlayerProcessMutexDeadlock\":\"True\",\"FFlagImprovedJoinScriptFlow\":\"True\",\"FFlagRCCLoadFMOD\":\"True\",\"FFlagStudioDeadCodeOnMouseDown\":\"True\",\"DFFlagFireCharacterAddedAfterSpawn\":\"False\",\"FFlagChatVisiblePropertyEnabled\":\"True\",\"FFlagChatLayoutChange\":\"False\",\"FFlagCorescriptNewLoadChat\":\"True\",\"DFLogLuaTypeErrors\":\"4\",\"DFFlagLuaInstanceBridgeNewCamelCaseFixerEnabled\":\"True\",\"FFlagConstraintUIEnabled\":\"True\",\"FFlagStudioSetObjectsFromPropertiesWindow\":\"True\",\"FFlagStudioPromptWhenInsertingConstraints\":\"True\",\"DFFlagFixExperimentalSolverSetter\":\"True\",\"DFFlagShowRedForAutoJointsForPartsWithConstraint\":\"True\",\"FFlagTrackOriginalClientID\":\"True\",\"FFlagStudioHiddenPropertyCrashFixEnabled\":\"True\",\"FFlagStudioPropertyChangedSignalHandlerFix\":\"True\",\"FFlagStudioScriptAnalysisGetOrCreateRefactoring\":\"True\",\"DFFlagAllowHttpServiceInTeamCreate\":\"True\",\"DFFlagAllowRequireByAssetIdInTeamCreate\":\"True\",\"DFFlagLuaSignalCamelCaseAPI\":\"True\",\"FFlagFixLogManagerWritingToTempDir\":\"True\",\"FFlagTrackModuleScripts\":\"True\",\"FFlagUserJumpButtonPositionChange\":\"True\",\"FFlagLoadCommonModules\":\"True\",\"FFlagMouseCommandChangedSignalEnabled\":\"True\",\"DFIntInfluxTattletalePerUserHundredthsPercent\":\"1\",\"DFIntInfluxTattletalePerEventHundredthsPercent\":\"2000\",\"DFIntInfluxTattletaleCooldownSeconds\":\"300\",\"DFFlagSendHttpInfluxDatabaseField\":\"True\",\"FFlagGraphicsD3D11HandleDeviceRemoved\":\"True\",\"FFlagFixPlayerProcessMutexDeadlockForReal\":\"True\",\"FFlagNetworkKeepItemPools\":\"True\",\"FLogNetworkItemQueueDtor\":\"1\",\"FFlagFixLoadingScreenAngle\":\"False\",\"FFlagFixStudioInGamePaste\":\"True\",\"FFlagStudioSetViewportSizeOfClone\":\"True\",\"FFlagStudioTreeWidgetPotentialMemoryLeak\":\"True\",\"FFlagStudioEnableLayersForNSView\":\"True\",\"FFlagEnableViewportScaling\":\"True\",\"FFlagStudioDisableScrollingOnEarlyMac\":\"True\",\"FFlagPerformanceStatsCollectionEnabled\":\"True\",\"FFlagStudioStopSoundPlaybackAfterRemoval\":\"True\",\"FFlagStudioAllowSoundDraggingFromToolbox\":\"True\",\"FFlagStudioRelocateSoundJob\":\"True\",\"FFlagLuaDebugProfileEnabled\":\"True\",\"FFlagRemoveSoundServiceSoundDisabledProperty\":\"True\",\"FFlagRecordInGameDeaths\":\"False\",\"FFlagStudio3DGridUseAALines\":\"False\",\"FFlagFixIsCurrentlyVisibleSurfaceGuis\":\"True\",\"FFlagSurfaceGuiObjectEnabledCheck\":\"True\",\"FStringPlaceFilter_InterpolationAwareTargetTime\":\"True;249779150;333368740;444708274;64542766;248207867;171391948;360589910;388599755;163865146;127243303;162537373;6597705;332248116;348681325;196235086;13822889;189707\",\"DFFlagUseR15Character3\":\"True\",\"DFFlagAllowCustomR15Character\":\"True\",\"DFFlagFixDoubleJointR15Character\":\"True\",\"DFFlagFixR15SphereHead\":\"False\",\"DFFlagUseR15SwimFreestyle\":\"True\",\"DFFlagFixBodyColorsR15\":\"True\",\"DFFlagPlayerDescendantsDeleteOnDisconnectOff\":\"True\",\"DFFlagSpringConstraintInGameAdornFixEnabled\":\"True\",\"DFFlagDontPrintMalformedUrls\":\"True\",\"DFFlagUseMultiFormatCharacterAppearanceLoading\":\"True\",\"FFlagStudioFlycamAppBridgeFix\":\"True\",\"FFlagAllowCopyUnArchivableObjects\":\"True\",\"FFlagStudioReduceTeamCreateStuttering\":\"True\",\"DFFlagPGSSolverUsesIslandizableCode\":\"True\",\"DFFlagResetScreenGuiEnabled\":\"True\",\"FFlagGraphicsD3DPointOne\":\"True\",\"FFlagGraphicsNoMainDepth\":\"True\",\"FFlagCollectClientIDUpdateStatistics\":\"True\",\"FFlagStudioFixTestApis\":\"True\",\"FFlagAllowInsertConstrainedValuesAnywhere\":\"True\",\"FFlagStudioResizeMeshPartOnImport\":\"True\",\"FFlagStudioReportVitalParameters\":\"True\",\"FFlagSoundGroupsAndEffectsEnabled\":\"True\",\"FFlagSoundscapeReplicateChildren\":\"True\",\"FFlagLoadCorescriptsPlatformDefMode\":\"True\",\"FFlagEnableGetHitWhitelist\":\"True\",\"FFlagStudioUpdatePropertiesWithoutJob\":\"True\",\"FFlagOverrideTypeFunction\":\"True\",\"DFFlagEnableBindToClose\":\"True\",\"FFlagStudioFixUndeletingSoundCausesPlayback\":\"True\",\"FFlagServerSenderDontSendInterpolatedPhysics\":\"False\",\"FFlagGraphicsD3D9ComputeIndexRange\":\"True\",\"FFlagCrashOnScriptCloseFixEnabled\":\"True\",\"FFlagShowCoreGUIInExplorer\":\"True\",\"FFlagStudioUseServerConfigurer\":\"True\",\"FFlagUDim2LerpEnabled\":\"True\",\"FFlagDisableLayersForNSViewOnEarlyMac\":\"True\",\"FFlagStudioCorrectForRetinaScreensOnEarlyMac\":\"True\",\"FFlagStudioConsistentGuiInitalisation\":\"True\",\"FFlagStudioSanitizeInstancesOnLoad\":\"True\",\"FFlagStudioOnlyUpdateTeamTestActionsIfChanged\":\"True\",\"FFlagChatServiceReplicates\":\"True\",\"DFFlagICMPPingHundrethsPercentage\":\"100\",\"DFFlagUsePasiveOnlyForBind\":\"False\",\"DFFlagFavorIPV4Connections\":\"False\",\"DFFlagUsegetFamilyandMapAddress\":\"False\",\"FFlagBetterPlaceLauncherStatusHandling\":\"True\",\"FStringPlaceFilter_NewLayoutAndConstraintsEnabled\":\"True;534842009;20213796;379132413;485971234;515782100;248207867;360699282;498699944;540764930;534808604;520456996;552894983;551169796;560164377;599021441;609763195;609918169;599392478;614429353;337448601;615210477;606827239;19481228;19827397;26953764;561540866;20397851;626302497;402593749;589006000;461274216;129419469;478459751;460710135;464914388;481987774;610775332;567211827;636396993\",\"FFlagInformClientInsertFiltering\":\"True\",\"FStringClientInsertFilterMoreInfoUrl\":\"http://devforum.roblox.com/t/coming-changes-to-insert-service/30327\",\"FFlagSoundChannelOnAncestorChangedUseGameLaunchIntent\":\"True\",\"DFFlagAllowResetButtonCustomization\":\"True\",\"FFlagTattletaleFixTextValue\":\"True\",\"DFIntInfluxTattletaleInstancePathMaxLength\":\"200\",\"DFFlagFixRetriesExhaustedHandling\":\"True\",\"DFFlagBetterGetPlayerPlaceInstanceError\":\"True\",\"DFFlagCharacterScriptsLoadingRefactor\":\"True\",\"FFlagStudioPropertyWidgetRemoveUpdateEvents\":\"True\",\"FFlagStudioUserNotificationIgnoreSequenceNumber\":\"True\",\"FFlagStudioOnlyOneToolboxPreviewAtATime\":\"True\",\"FFlagStudioFixPauseDuringLoad\":\"True\",\"DFFlagStudioUseNewActiveToolEffect\":\"True\",\"FFlagGraphicsD3D11PickAdapter\":\"True\",\"FFlagChatFilterWorksLocally\":\"True\",\"FFlagFilterMessageWithCallbackNoTryCatch\":\"True\",\"FStringPlaceFilter_SetPhysicsToLastRealStateWhenBecomingOwner\":\"True;13822889;189707\",\"FFlagMetaliOS\":\"True\",\"FFlagUseNewAppBridgeIOS\":\"True\",\"FFlagTextBoundRespectTextScaled\":\"True\",\"FFlagRenderFastResolve\":\"True\",\"DFFlagLoadingGuiTeleportCrashFix\":\"True\",\"FFlagPluginSaveSelection\":\"True\",\"FFlagHandleSoundPreviewWidgetWithNoSelectedSound\":\"True\",\"FFlagDontSwallowInputForStudioShortcuts\":\"True\",\"FFlagStudioFireStickyMouseCommandChangedOnly\":\"True\",\"FFlagStudioDisableEditingCurrentEditor\":\"True\",\"FFlagFixCorruptionInLogFiles\":\"True\",\"FFlagStudioLockScriptsWithoutBlocking\":\"True\",\"FFlagSetPhysicsToLastRealStateWhenBecomingOwner\":\"True\",\"FFlagInterpolationAwareTargetTime\":\"True\",\"DFFlagServerSenderDontSendInterpolatedPhysics\":\"True\",\"FFlagSyncRenderingAndPhysicsInterpolation\":\"True\",\"DFIntTargetTimeDelayFacctorTenths\":\"20\",\"FFlagNewIncomingPhysicsManagement\":\"True\",\"DFFlagGetAssetIdsFromPackageAPI\":\"True\",\"DFFlagGoodbyeChoiceActiveProperty\":\"True\",\"FFlagAllowResizeRenderBufferiOS\":\"True\",\"FIntEnableAvatarEditoriOS\":\"100\",\"FIntEnableAvatarEditorAndroid\":\"1\",\"FIntAvatarEditorAndroidRollout\":\"1\",\"DFFlagFixScaledR15Physics\":\"True\",\"DFFlagScaleR15Character\":\"True\",\"FFlagCorescriptSetCoreChatActiveEnabled\":\"True\",\"FFlagOnlyShowHealthWhenDamaged\":\"True\",\"FFlagUserChatPrivacySetting\":\"True\",\"FFlagCorescriptNewChatSetCoresEnabled\":\"True\"}";
        }

        [HttpGet("v1/settings/rcc")]
        public dynamic ParseApp()
        {
            return "{\"AllowVideoPreRoll\":\"True\",\"AxisAdornmentGrabSize\":\"12\",\"CaptureCountersIntervalInMinutes\":\"5\",\"CaptureMFCStudioCountersEnabled\":\"True\",\"CaptureQTStudioCountersEnabled\":\"True\",\"DFFlagAccessUserFeatureSetting\":\"True\",\"DFFlagAccessoriesAndAttachments\":\"True\",\"DFFlagActivatePGSMechanicalJoints\":\"True\",\"DFFlagAddPlaceIdToAnimationRequests\":\"True\",\"DFFlagAddRequestIdToDeveloperProductPurchases\":\"True\",\"DFFlagAdjustFloorForce\":\"True\",\"DFFlagAllowAllUsersToUseHttpService\":\"True\",\"DFFlagAllowBindActivate\":\"True\",\"DFFlagAllowCustomR15Character\":\"True\",\"DFFlagAllowFullModelsWhenLoadingModules\":\"True\",\"DFFlagAllowHideHudShortcut\":\"False\",\"DFFlagAllowHttpServiceInTeamCreate\":\"True\",\"DFFlagAllowHumanoidDecalTransparency\":\"True\",\"DFFlagAllowModuleLoadingFromAssetId\":\"True\",\"DFFlagAllowMoveToInMouseLookMove\":\"True\",\"DFFlagAllowRequireByAssetIdInTeamCreate\":\"True\",\"DFFlagAllowResetButtonCustomization\":\"True\",\"DFFlagAllowTeleportFromServer\":\"True\",\"DFFlagAlwaysUseHumanoidMass\":\"True\",\"DFFlagAnimationAllowProdUrls\":\"True\",\"DFFlagAnimationEasingStylesEnabled\":\"True\",\"DFFlagAnimationFailedToLoadContext\":\"True\",\"DFFlagAnimationFormatAssetId\":\"True\",\"DFFlagApiCapitalizationChanges\":\"True\",\"DFFlagApiDictionaryCompression\":\"True\",\"DFFlagAppendTrackerIdToTeleportUrl\":\"True\",\"DFFlagAvatarFetchCanLoadCharacterAppearanceFix\":\"True\",\"DFFlagBackTabInputInStudio\":\"True\",\"DFFlagBadTypeOnConnectErrorEnabled\":\"True\",\"DFFlagBetterGetPlayerPlaceInstanceError\":\"True\",\"DFFlagBlendPosesWithIsOver\":\"True\",\"DFFlagBlockCustomHttpHeaders\":\"False\",\"DFFlagBlockUsersInLuaChat\":\"True\",\"DFFlagBodyMoverParentingFix\":\"True\",\"DFFlagBrickColorParseNonDeprecatedMatchEnabled\":\"True\",\"DFFlagBroadcastServerOnAllInterfaces\":\"True\",\"DFFlagBulletFixCacheReuse\":\"True\",\"DFFlagCFrameRightAndUpVectors\":\"True\",\"DFFlagCSGDictionaryReplication\":\"True\",\"DFFlagCSGLeftoverDataFix\":\"True\",\"DFFlagCSGPhysicsDeserializeRefactor\":\"True\",\"DFFlagCSGPhysicsErrorCatchingEnabled\":\"True\",\"DFFlagCSGPhysicsNanPrevention\":\"True\",\"DFFlagCSGPhysicsRecalculateBadContactsInConnectors\":\"True\",\"DFFlagCSGPhysicsRefreshContactsManually\":\"True\",\"DFFlagCSGPhysicsSphereRotationIdentity\":\"True\",\"DFFlagCSGPreventCrashesWhenPartOperationsNotInDataModel\":\"True\",\"DFFlagCSGPreventNoContextCSGCrashes\":\"False\",\"DFFlagCSGShrinkForMargin\":\"True\",\"DFFlagCachedPoseInitialized\":\"True\",\"DFFlagCatchThrottledVelocityComponents\":\"True\",\"DFFlagCharacterAppearanceLoadedEnabled\":\"True\",\"DFFlagCharacterScriptsLoadingRefactor\":\"True\",\"DFFlagChatServiceFilterStringForPlayerFromAndToStudioBypass\":\"True\",\"DFFlagCheckApiAccessInTransactionProcessing\":\"True\",\"DFFlagCheckDataModelOnTeleportRetry\":\"True\",\"DFFlagCheckForHeadHit\":\"False\",\"DFFlagCheckStudioApiAccess\":\"True\",\"DFFlagClampRocketThrustOnPGS\":\"True\",\"DFFlagCleanCacheMoveMutex\":\"True\",\"DFFlagCleanUpInterpolationTimestamps\":\"True\",\"DFFlagClearAllChildrenUseDestroy\":\"True\",\"DFFlagClearFailedUrlsWhenClearingCacheEnabled\":\"True\",\"DFFlagClearPlayerReceivingServerLogsOnLeave\":\"True\",\"DFFlagClickDetectorReplicate\":\"True\",\"DFFlagClientAdditionalPOSTHeaders\":\"True\",\"DFFlagCloudEditByPassCheckForServer\":\"True\",\"DFFlagCloudEditRemoveEditorOnPlayerRemove\":\"True\",\"DFFlagCloudEditSupportImmediatePublish\":\"True\",\"DFFlagConfigurableFallenPartDestroyHeight\":\"True\",\"DFFlagConfigureInsertServiceFromSettings\":\"True\",\"DFFlagConsoleCodeExecutionEnabled\":\"True\",\"DFFlagConstructModerationFilterTextParamsAndHeadersUseLegacyFilterParams\":\"False\",\"DFFlagContactManagerOptimizedQueryExtents\":\"True\",\"DFFlagContentProviderHttpCaching\":\"True\",\"DFFlagCoreGuiCustomizationApi\":\"True\",\"DFFlagCorrectFloorNormal\":\"True\",\"DFFlagCorrectlyReportSpeedOnRunStart\":\"True\",\"DFFlagCrashOnNetworkPacketError\":\"False\",\"DFFlagCrashTouchTransmitterIfRefDtor\":\"False\",\"DFFlagCreateHumanoidRootNode\":\"True\",\"DFFlagCreatePlaceEnabledForEveryone\":\"True\",\"DFFlagCreatePlayerGuiEarlier\":\"True\",\"DFFlagCreatePlayerGuiLocal\":\"False\",\"DFFlagCreateSeatWeldOnServer\":\"True\",\"DFFlagCrispFilteringEnabled\":\"False\",\"DFFlagCrossPacketCompression\":\"True\",\"DFFlagCustomEmitterInstanceEnabled\":\"True\",\"DFFlagCustomTeleportLoadingScreen\":\"True\",\"DFFlagCyclicExecutiveFixNonCyclicJobRun\":\"True\",\"DFFlagDE6959Fixed\":\"True\",\"DFFlagDataStoreAllowedForEveryone\":\"True\",\"DFFlagDataStoreDisableReFetchingRecentKeys\":\"True\",\"DFFlagDataStoreUrlEncodingEnabled\":\"True\",\"DFFlagDataStoreUseUForGlobalDataStore\":\"True\",\"DFFlagDebrisServiceUseDestroy\":\"True\",\"DFFlagDebugLogNetworkErrorToDB\":\"False\",\"DFFlagDeferredTouchReplication\":\"True\",\"DFFlagDeserializePacketThreadEnabled\":\"True\",\"DFFlagDesiredAltitudeDefaultInf\":\"True\",\"DFFlagDisableBackendInsertConnection\":\"True\",\"DFFlagDisableCharacterRequest\":\"True\",\"DFFlagDisableGetKeyframeSequence\":\"False\",\"DFFlagDisableMovementHistory\":\"True\",\"DFFlagDisableProcessPacketsJobReschedule\":\"True\",\"DFFlagDisableRequestMarker\":\"True\",\"DFFlagDisableTeleportConfirmation\":\"True\",\"DFFlagDisableTimeoutDuringJoin\":\"True\",\"DFFlagDisallowHopperServerScriptReplication\":\"True\",\"DFFlagDisplayTextBoxTextWhileTypingMobile\":\"False\",\"DFFlagDoNotForwardClientTimestamp\":\"True\",\"DFFlagDoNotHoldTagItemsForInitialData\":\"True\",\"DFFlagDoUpdateStepDetachedChannels\":\"True\",\"DFFlagDontCacheHumanoids\":\"True\",\"DFFlagDontPrintMalformedUrls\":\"True\",\"DFFlagDontReorderScreenGuisWhenDescendantRemoving\":\"True\",\"DFFlagDontUseInsertServiceOnAnimLoad\":\"True\",\"DFFlagDraggerUsesNewPartOnDuplicate\":\"True\",\"DFFlagDuplicateInstanceReferenceFix\":\"True\",\"DFFlagDynamicGravity\":\"True\",\"DFFlagElasticEasingUseTwoPi\":\"True\",\"DFFlagEnableAdColony\":\"False\",\"DFFlagEnableAnimationInformationAccess\":\"True\",\"DFFlagEnableAnimationSetTimePosition\":\"True\",\"DFFlagEnableAnimationTrackExtendedAPI\":\"True\",\"DFFlagEnableBindToClose\":\"True\",\"DFFlagEnableChatTestingInStudio\":\"True\",\"DFFlagEnableClimbingDirection\":\"True\",\"DFFlagEnableGetPlayingAnimationTracks\":\"True\",\"DFFlagEnableGoodbyeDialog\":\"True\",\"DFFlagEnableHipHeight\":\"True\",\"DFFlagEnableHumanoidDisplayDistances\":\"True\",\"DFFlagEnableHumanoidSetStateEnabled\":\"True\",\"DFFlagEnableJointCache\":\"False\",\"DFFlagEnableMarketPlaceServiceCaching\":\"True\",\"DFFlagEnableMobileAutoJumpFlag\":\"True\",\"DFFlagEnableNPCServerAnimation\":\"True\",\"DFFlagEnableParticleBurst\":\"True\",\"DFFlagEnableParticleEmissionDirection\":\"True\",\"DFFlagEnableParticleNewBoundingBox\":\"True\",\"DFFlagEnableParticlePartLock\":\"True\",\"DFFlagEnableParticleVelocityInheritance\":\"True\",\"DFFlagEnablePreloadAsync\":\"True\",\"DFFlagEnableRapidJSONParser\":\"True\",\"DFFlagEnableReverseAnimations\":\"True\",\"DFFlagEnableShowStatsLua\":\"True\",\"DFFlagEnsureSoundPosIsUpdated\":\"True\",\"DFFlagEquipClonedToolFix\":\"True\",\"DFFlagExplicitPostContentType\":\"True\",\"DFFlagFMODSetAccurateTime\":\"True\",\"DFFlagFastClone\":\"True\",\"DFFlagFastFilterHumanoidParts\":\"False\",\"DFFlagFavorIPV4Connections\":\"False\",\"DFFlagFilterAddSelectionToSameDataModel\":\"False\",\"DFFlagFilterStreamingProps\":\"True\",\"DFFlagFilteringEnabledDialogFix\":\"True\",\"DFFlagFindFirstChildOfClassEnabled\":\"True\",\"DFFlagFireCharacterAddedAfterSpawn\":\"False\",\"DFFlagFirePlayerAddedAndPlayerRemovingOnClient\":\"True\",\"DFFlagFireSelectionChangeOncePerChange\":\"True\",\"DFFlagFireStoppedAnimSignal\":\"True\",\"DFFlagFixAllCharacterStreaming\":\"True\",\"DFFlagFixAnchoredSeatingPosition\":\"True\",\"DFFlagFixAnimationControllerAnimator\":\"True\",\"DFFlagFixAnimationPhaseInitialization\":\"True\",\"DFFlagFixBallInsideBlockCollision\":\"True\",\"DFFlagFixBodyColorsR15\":\"True\",\"DFFlagFixBufferZoneContainsCheck\":\"False\",\"DFFlagFixBytesOnJoinReporting\":\"True\",\"DFFlagFixDoubleJointR15Character\":\"True\",\"DFFlagFixExperimentalSolverSetter\":\"True\",\"DFFlagFixFallenPartsNotDeleted\":\"True\",\"DFFlagFixGhostClimb\":\"True\",\"DFFlagFixHealthReplication\":\"True\",\"DFFlagFixInstanceParentDesyncBug\":\"True\",\"DFFlagFixJointReparentingDE11763\":\"True\",\"DFFlagFixJumpGracePeriod\":\"True\",\"DFFlagFixJumpRequestOnLand\":\"True\",\"DFFlagFixLadderClimbSpeed\":\"True\",\"DFFlagFixLuaMoveDirection\":\"True\",\"DFFlagFixMatrixToAxisAngle\":\"True\",\"DFFlagFixNestedAnimators\":\"True\",\"DFFlagFixParticleDistribution\":\"True\",\"DFFlagFixProcessReceiptValueTypes\":\"True\",\"DFFlagFixR15BodyPhysics\":\"True\",\"DFFlagFixR15SphereHead\":\"False\",\"DFFlagFixRetriesExhaustedHandling\":\"True\",\"DFFlagFixRotatedHorizontalScrollBar\":\"True\",\"DFFlagFixScaledR15Physics\":\"True\",\"DFFlagFixScriptableCameraRoll\":\"True\",\"DFFlagFixScrollingOffSurfaceGUIs\":\"True\",\"DFFlagFixSeatingWhileSitting\":\"True\",\"DFFlagFixShapeChangeBug\":\"True\",\"DFFlagFixSlowLadderClimb\":\"True\",\"DFFlagFixTimePositionReplication\":\"True\",\"DFFlagFixTimeStampsForRunningNoThrottle\":\"True\",\"DFFlagFixTouchEndedReporting\":\"False\",\"DFFlagFixYieldThrottling\":\"True\",\"DFFlagFoldersInGUIs\":\"True\",\"DFFlagFormFactorDeprecated\":\"False\",\"DFFlagFullscreenEnabledWhileRecording\":\"True\",\"DFFlagFullscreenRefocusingFix\":\"True\",\"DFFlagGamepadProcessMouseEvents\":\"False\",\"DFFlagGetAssetIdsFromPackageAPI\":\"True\",\"DFFlagGetCharacterAppearanceEnabled\":\"True\",\"DFFlagGetFocusedTextBoxEnabled\":\"True\",\"DFFlagGetFriendsEnabled\":\"True\",\"DFFlagGetFriendsOnlineEnabled\":\"True\",\"DFFlagGetGroupInfoEnabled\":\"True\",\"DFFlagGetGroupRelationsEnabled\":\"True\",\"DFFlagGetGroupsAsyncEnabled\":\"True\",\"DFFlagGetLastestAssetVersionEnabled\":\"True\",\"DFFlagGetLocalTeleportData\":\"True\",\"DFFlagGetUserIdEnabled\":\"True\",\"DFFlagGetUserNameEnabled\":\"True\",\"DFFlagGoodbyeChoiceActiveProperty\":\"True\",\"DFFlagGuiBase3dReplicateColor3WithBrickColor\":\"True\",\"DFFlagHttpCurlDomainTrimmingWithBaseURL\":\"False\",\"DFFlagHttpCurlHandle301\":\"True\",\"DFFlagHttpCurlSanitizeUrl\":\"True\",\"DFFlagHttpDelaySendInfluxStats\":\"True\",\"DFFlagHttpReportStatistics\":\"True\",\"DFFlagHttpStatusCodeErrorIncludeBody\":\"True\",\"DFFlagHttpZeroLatencyCaching\":\"True\",\"DFFlagHumanoidCheckForNegatives\":\"True\",\"DFFlagHumanoidCookieRecursive\":\"True\",\"DFFlagHumanoidFeetIsPlastic\":\"True\",\"DFFlagHumanoidFloorDetectTeleport\":\"True\",\"DFFlagHumanoidFloorForceBufferZone\":\"False\",\"DFFlagHumanoidFloorManualDeltaUpdateManagment\":\"True\",\"DFFlagHumanoidFloorManualFrictionLimitation\":\"True\",\"DFFlagHumanoidFloorPVUpdateSignal\":\"True\",\"DFFlagHumanoidJumpPower\":\"True\",\"DFFlagHumanoidMoveToDefaultValueEnabled\":\"True\",\"DFFlagHumanoidStandOnSeatDestroyed\":\"True\",\"DFFlagICMPPingHundrethsPercentage\":\"100\",\"DFFlagImageFailedToLoadContext\":\"True\",\"DFFlagImprovedKick\":\"True\",\"DFFlagIncreaseScrollWheelMultiplyTime\":\"True\",\"DFFlagIncreaseSoundPositionClampLimit\":\"True\",\"DFFlagInfluxDb09Enabled\":\"True\",\"DFFlagInfluxOverTCP\":\"False\",\"DFFlagInfluxOverUDP\":\"True\",\"DFFlagInsertServiceForceLocalInTeamCreate\":\"True\",\"DFFlagInstancePredeleteNuke\":\"True\",\"DFFlagInterpolationTimingFix\":\"True\",\"DFFlagIsBestFriendsWithReturnFriendsWith\":\"True\",\"DFFlagIsUrlCheckAssetStringLength\":\"True\",\"DFFlagKeepXmlIdsBetweenLoads\":\"True\",\"DFFlagLegacyBodyColorsOnCharacterLoadFailure\":\"True\",\"DFFlagLimitScrollWheelMaxToHalfWindowSize\":\"True\",\"DFFlagListenForZVectorChanges\":\"True\",\"DFFlagListenPositionEnabled\":\"True\",\"DFFlagLiveColorUpdatesCanceling\":\"True\",\"DFFlagLoadAnimationsThroughInsertService\":\"True\",\"DFFlagLoadCoreModules\":\"True\",\"DFFlagLoadFileUseRegularHttp\":\"True\",\"DFFlagLoadInstancesAsyncUseDataModelTasks\":\"False\",\"DFFlagLoadSourceForCoreScriptsBeforeInserting\":\"False\",\"DFFlagLoadStarterGearEarlier\":\"False\",\"DFFlagLoadingGuiTeleportCrashFix\":\"True\",\"DFFlagLocalHumanoidSoundsEnabled\":\"True\",\"DFFlagLocalScriptSpawnPartAlwaysSetOwner\":\"True\",\"DFFlagLockViolationInstanceCrash\":\"False\",\"DFFlagLockViolationScriptCrash\":\"False\",\"DFFlagLogPacketErrorDetails\":\"False\",\"DFFlagLogServiceEnabled\":\"True\",\"DFFlagLoggingConsoleEnabled\":\"True\",\"DFFlagLookForDuplicateCoordinateFrameInBuffer\":\"True\",\"DFFlagLoopedDefaultHumanoidAnimation\":\"True\",\"DFFlagLuaCloseUpvalues\":\"True\",\"DFFlagLuaDisconnectFailingSlots\":\"False\",\"DFFlagLuaFixResumeWaiting\":\"True\",\"DFFlagLuaGcPerVm\":\"True\",\"DFFlagLuaInstanceBridgeNewCamelCaseFixerEnabled\":\"True\",\"DFFlagLuaLoadStringStrictSecurity\":\"True\",\"DFFlagLuaNoTailCalls\":\"True\",\"DFFlagLuaResumeSupportsCeeCalls\":\"True\",\"DFFlagLuaSignalCamelCaseAPI\":\"True\",\"DFFlagLuaYieldErrorNoResumeEnabled\":\"True\",\"DFFlagMacInferredCrashReporting\":\"True\",\"DFFlagMakeWebPendingFriendRequests\":\"True\",\"DFFlagMaskWeightCleared\":\"True\",\"DFFlagMaterialPropertiesDivideByZeroWeights\":\"True\",\"DFFlagMaterialPropertiesEnabled\":\"True\",\"DFFlagMaxPlayersEnabled\":\"True\",\"DFFlagMiddleMouseButtonEvent\":\"True\",\"DFFlagMoveToDontAlignToGrid\":\"True\",\"DFFlagMovingHumananoidWakesFloor\":\"True\",\"DFFlagNameOcculsionIgnoreTransparent\":\"True\",\"DFFlagNamesOccludedAsDefault\":\"True\",\"DFFlagNetworkFilterAllowToolWelds\":\"True\",\"DFFlagNetworkFixJoinDataItemOrder\":\"True\",\"DFFlagNetworkOwnerOptEnabled\":\"True\",\"DFFlagNetworkOwnershipRuleReplicates\":\"True\",\"DFFlagNetworkPendingItemsPreserveTimestamp\":\"True\",\"DFFlagNewLuaChatScript\":\"True\",\"DFFlagNoAnimationTrackState\":\"True\",\"DFFlagNoOwnershipLogicOnKernelJoint\":\"True\",\"DFFlagNoRunSteepSlope\":\"True\",\"DFFlagNoWalkAnimWeld\":\"False\",\"DFFlagNonBlockingTeleport\":\"True\",\"DFFlagNonEmptyPcallError\":\"True\",\"DFFlagNullCheckJointStepWithNullPrim\":\"True\",\"DFFlagNullStarterCharacterPrimaryPartFix\":\"True\",\"DFFlagOnCloseTimeoutEnabled\":\"True\",\"DFFlagOptimizeAnimator\":\"True\",\"DFFlagOrder66\":\"False\",\"DFFlagOrthonormalizeJointCoords\":\"True\",\"DFFlagOverrideScollingDisabledWhenRecalulateNeeded\":\"True\",\"DFFlagPGSSolverUsesIslandizableCode\":\"True\",\"DFFlagPGSWakeOtherAssemblyForJoints\":\"True\",\"DFFlagPGSWakeOtherIfOneAssemblyIsAwake\":\"False\",\"DFFlagPGSWakePrimitivesWithBodyMoverPropertyChanges\":\"True\",\"DFFlagPartForRegion3NoMaxLimit\":\"True\",\"DFFlagPartsStreamingEnabled\":\"True\",\"DFFlagPasteWithCapsLockOn\":\"True\",\"DFFlagPathfindingEnabled\":\"True\",\"DFFlagPhysicalPropMassCalcOnJoin\":\"False\",\"DFFlagPhysicsAggressiveSimRadiusReduction\":\"True\",\"DFFlagPhysicsAllowSimRadiusToDecreaseToOne\":\"True\",\"DFFlagPhysicsFPSTimerFix\":\"True\",\"DFFlagPhysicsFastSmoothTerrainUpdate\":\"True\",\"DFFlagPhysicsInvalidateContactCache\":\"True\",\"DFFlagPhysicsOptimizeAssemblyHistory\":\"True\",\"DFFlagPhysicsOptimizeBallBallContact\":\"True\",\"DFFlagPhysicsPacketAlwaysUseCurrentTime\":\"True\",\"DFFlagPhysicsSenderErrorCalcOpt\":\"True\",\"DFFlagPhysicsSenderSleepingUpdate\":\"True\",\"DFFlagPhysicsSenderThrottleBasedOnBufferHealth\":\"True\",\"DFFlagPhysicsSenderUseOwnerTimestamp\":\"False\",\"DFFlagPhysicsSkipUnnecessaryContactCreation\":\"True\",\"DFFlagPhysicsUseNewBulletContact\":\"True\",\"DFFlagPlaceValidation\":\"True\",\"DFFlagPlayerDescendantsDeleteOnDisconnectOff\":\"True\",\"DFFlagPlayerOwnsAssetFalseForInvalidUsers\":\"True\",\"DFFlagPreProcessTextBoxEvents\":\"True\",\"DFFlagPreferredPlayersEnabled\":\"True\",\"DFFlagPreventReturnOfElevatedPhysicsFPS\":\"True\",\"DFFlagProcessAcceleratorsBeforeGUINaviagtion\":\"True\",\"DFFlagProjectileOwnershipOptimization\":\"True\",\"DFFlagPromoteAssemblyModifications\":\"True\",\"DFFlagPromptPurchaseOnGuestHandledInCoreScript\":\"True\",\"DFFlagPushLuaWorldRayOriginToNearClipPlane\":\"True\",\"DFFlagRCCDE13316CrashFix\":\"True\",\"DFFlagRCCUseMarketplaceApiClient\":\"False\",\"DFFlagRaiseSendPriority\":\"False\",\"DFFlagRayHitMaterial\":\"True\",\"DFFlagRaycastReturnSurfaceNormal\":\"True\",\"DFFlagReadXmlCDataEnabled\":\"True\",\"DFFlagRealWinInetHttpCacheBypassingEnabled\":\"True\",\"DFFlagReconstructAssetUrlNew\":\"True\",\"DFFlagRecursiveWakeForBodyMovers\":\"True\",\"DFFlagReduceHumanoidBounce\":\"True\",\"DFFlagRejectHashesInLinkedSource\":\"True\",\"DFFlagRemoteFixDisconnectedPlayer\":\"True\",\"DFFlagRemoteValidateSubscribersError\":\"True\",\"DFFlagRemoveAdornFromBucketInDtor\":\"True\",\"DFFlagRemoveDataModelDependenceInWaitForChild\":\"True\",\"DFFlagRenderSteppedServerExceptionEnabled\":\"True\",\"DFFlagReplicateAnimationSpeed\":\"True\",\"DFFlagReplicatorCheckBadRebinding\":\"True\",\"DFFlagReplicatorWorkspaceProperties\":\"True\",\"DFFlagReportElevatedPhysicsFPSToGA\":\"True\",\"DFFlagResetScreenGuiEnabled\":\"True\",\"DFFlagRestoreTransparencyOnToolChange\":\"False\",\"DFFlagRevisedClientJoinMetrics\":\"True\",\"DFFlagRobloxAnalyticsTrackingEnabled\":\"False\",\"DFFlagSanitizeKeyframeUrl\":\"True\",\"DFFlagSanitizeLoadingGUICorrected\":\"True\",\"DFFlagScaleR15Character\":\"True\",\"DFFlagScopedMutexOnJSONParser\":\"True\",\"DFFlagScreenShotDuplicationFix\":\"True\",\"DFFlagScriptContextGuardAgainstCStackOverflow\":\"True\",\"DFFlagScriptDefaultSourceIsEmpty\":\"True\",\"DFFlagScriptExecutionContextApi\":\"True\",\"DFFlagScrollingFrameDraggingFix\":\"True\",\"DFFlagSendHttpBodyOnFailure\":\"True\",\"DFFlagSendHttpInfluxDatabaseField\":\"True\",\"DFFlagSendHumanoidTouchedSignal\":\"True\",\"DFFlagSeparateBulletNarrowPhaseAndMidStepUpdates\":\"True\",\"DFFlagServerSenderDontSendInterpolatedPhysics\":\"True\",\"DFFlagSetHumanoidRegardlessOfNetworkOwnership\":\"True\",\"DFFlagSetNetworkOwnerAPIEnabled\":\"True\",\"DFFlagSetNetworkOwnerAPIEnabledV2\":\"True\",\"DFFlagSetNetworkOwnerCanSetCheck\":\"True\",\"DFFlagSetNetworkOwnerFixAnchoring\":\"True\",\"DFFlagSetNetworkOwnerFixAnchoring2\":\"True\",\"DFFlagSetRenderedFrameOnClumpChanged\":\"True\",\"DFFlagShowFormFactorDeprecatedWarning\":\"False\",\"DFFlagShowRedForAutoJointsForPartsWithConstraint\":\"True\",\"DFFlagSimpleHermiteSplineInterpolate\":\"False\",\"DFFlagSmoothTerrainDebounceUpdates\":\"True\",\"DFFlagSmoothTerrainPhysicsExpandPrimitiveOptimal\":\"True\",\"DFFlagSmoothTerrainPhysicsRayAabbExact\":\"True\",\"DFFlagSmoothTerrainWorldToCellUseDiagonals\":\"True\",\"DFFlagSmoothTerrainWriteVoxelsOccupancyFix\":\"True\",\"DFFlagSmootherVehicleSeatControlSystem\":\"True\",\"DFFlagSoundEndedEnabled\":\"True\",\"DFFlagSoundFailedToLoadContext\":\"True\",\"DFFlagSoundV2LoadUseParamContext\":\"True\",\"DFFlagSoundV2LoadedRunCallbacks\":\"True\",\"DFFlagSoundV2LogOnSetSoundId\":\"True\",\"DFFlagSpawnPointEnableProperty\":\"True\",\"DFFlagSpringConstraintInGameAdornFixEnabled\":\"True\",\"DFFlagStackTraceHasNewLines\":\"True\",\"DFFlagStarterGuiMethodsWarnServer\":\"True\",\"DFFlagStarterGuiPropertiesReplicate\":\"True\",\"DFFlagStopUsingMaskWeight\":\"True\",\"DFFlagStreamLargeAudioFiles\":\"True\",\"DFFlagStudioFixPastingDecalsIntoMultiple\":\"True\",\"DFFlagStudioUseNewActiveToolEffect\":\"True\",\"DFFlagSupportCsrfHeaders\":\"True\",\"DFFlagSupportNamedAssetsShortcutUrl\":\"True\",\"DFFlagTaskSchedulerFindJobOpt\":\"True\",\"DFFlagTaskSchedulerNotUpdateErrorOnPreStep\":\"True\",\"DFFlagTaskSchedulerUpdateJobPriorityOnWake\":\"True\",\"DFFlagTeamCreateDoNotReplicateShowDevGuiProp\":\"True\",\"DFFlagTeleportSignalConnectOnServiceProvider\":\"True\",\"DFFlagTextBoxIsFocusedEnabled\":\"True\",\"DFFlagTextScaleDontWrapInWords\":\"True\",\"DFFlagToolRequiresHandleProperty\":\"True\",\"DFFlagTrackLastDownGUI\":\"True\",\"DFFlagTrackPhysicalPropertiesGA\":\"True\",\"DFFlagTrackTimesScriptLoadedFromLinkedSource\":\"True\",\"DFFlagTurnOffFakeEventsForCAS\":\"True\",\"DFFlagTurnOffFakeEventsForInputEvents\":\"True\",\"DFFlagUpdateCameraTarget\":\"True\",\"DFFlagUpdateHermiteLastFrameWhenUpdatePrevFrame\":\"True\",\"DFFlagUpdateHumanoidNameAndHealth\":\"True\",\"DFFlagUpdateHumanoidSimBodyInComputeForce\":\"True\",\"DFFlagUpdateTimeOnDelayedSamples\":\"False\",\"DFFlagUrlReconstructToAssetGame\":\"False\",\"DFFlagUrlReconstructToAssetGameSecure\":\"False\",\"DFFlagUse9FrameBackgroundTransparency\":\"True\",\"DFFlagUseApiProxyThrottling\":\"True\",\"DFFlagUseAssetTypeHeader\":\"True\",\"DFFlagUseAvatarFetchAPI\":\"False\",\"DFFlagUseCanManageApiToDetermineConsoleAccess\":\"False\",\"DFFlagUseCharacterRootforCameraTarget\":\"True\",\"DFFlagUseComSiftUpdatedWebChatFilterParamsAndHeader\":\"False\",\"DFFlagUseDecalLocalTransparencyModifier\":\"True\",\"DFFlagUseFolder\":\"True\",\"DFFlagUseGameAvatarTypeEnum\":\"False\",\"DFFlagUseHermiteSplineInterpolation\":\"True\",\"DFFlagUseHttpsForAllCalls\":\"True\",\"DFFlagUseImageColor\":\"True\",\"DFFlagUseImprovedLadderClimb\":\"True\",\"DFFlagUseIntersectingOthersForSpawnEnabled\":\"True\",\"DFFlagUseLuaCameraAndControl\":\"True\",\"DFFlagUseLuaPCInput\":\"True\",\"DFFlagUseMeshPartR15\":\"True\",\"DFFlagUseModerationFilterTextV2\":\"True\",\"DFFlagUseMultiFormatCharacterAppearanceLoading\":\"True\",\"DFFlagUseNewAnalyticsApi\":\"True\",\"DFFlagUseNewBubbleSkin\":\"True\",\"DFFlagUseNewDataStoreLogging\":\"True\",\"DFFlagUseNewFetchFriendsFunction\":\"True\",\"DFFlagUseNewFullscreenLogic\":\"True\",\"DFFlagUseNewHumanoidHealthGui\":\"True\",\"DFFlagUseNewPersistenceSubdomain\":\"True\",\"DFFlagUseNewSounds\":\"True\",\"DFFlagUseNewTextBoxLogic\":\"True\",\"DFFlagUsePGSSolverSpringConstantScale\":\"True\",\"DFFlagUsePasiveOnlyForBind\":\"False\",\"DFFlagUsePlayerInGroupLuaChat\":\"True\",\"DFFlagUsePlayerScripts\":\"True\",\"DFFlagUsePlayerSpawnPoint\":\"True\",\"DFFlagUsePointsNewBatchingImpl\":\"True\",\"DFFlagUsePreferredSpawnInPlaySoloTeleport\":\"True\",\"DFFlagUseR15Character\":\"True\",\"DFFlagUseR15Character3\":\"True\",\"DFFlagUseR15SwimFreestyle\":\"True\",\"DFFlagUseSaferChatMetadataLoading\":\"True\",\"DFFlagUseServerCoreScripts\":\"True\",\"DFFlagUseSpawnPointOrientation\":\"True\",\"DFFlagUseStarterPlayer\":\"True\",\"DFFlagUseStarterPlayerCharacter\":\"True\",\"DFFlagUseStarterPlayerCharacterScripts\":\"True\",\"DFFlagUseStarterPlayerGA\":\"True\",\"DFFlagUseStarterPlayerHumanoid\":\"True\",\"DFFlagUseStrongerGroundControl\":\"True\",\"DFFlagUseTerrainCustomPhysicalProperties\":\"True\",\"DFFlagUseW3CURIParser\":\"True\",\"DFFlagUseYPCallInsteadOfPCallEnabled\":\"True\",\"DFFlagUsegetFamilyandMapAddress\":\"False\",\"DFFlagUserAccessUserSettings\":\"True\",\"DFFlagUserHttpAPIEnabled\":\"True\",\"DFFlagUserInputServiceProcessOnRender\":\"True\",\"DFFlagUserServerFollowers\":\"True\",\"DFFlagUserUseLuaVehicleController\":\"True\",\"DFFlagVIPServerOwnerIdEnabled\":\"True\",\"DFFlagValidateCharacterAppearanceUrl\":\"False\",\"DFFlagValidateSetCharacter\":\"True\",\"DFFlagVariableHeartbeat\":\"True\",\"DFFlagVideoCaptureTeleportFix\":\"False\",\"DFFlagWaitForToolHandleToEquip\":\"True\",\"DFFlagWebParserDisableInstances\":\"False\",\"DFFlagWebParserEnforceASCIIEnabled\":\"True\",\"DFFlagWindowsInferredCrashReporting\":\"True\",\"DFFlagWorkspaceSkipTerrainRaycastForSurfaceGui\":\"True\",\"DFFlagWriteXmlCDataEnabled\":\"True\",\"DFFlagZeroVelocityOnDelayedSamples\":\"True\",\"DFIntAndroidInfluxHundredthsPercentage\":\"0\",\"DFIntBulletContactBreakOrthogonalThresholdPercent\":\"200\",\"DFIntBulletContactBreakThresholdPercent\":\"200\",\"DFIntDataStoreMaxValueSize\":\"262144\",\"DFIntDraggerMaxMovePercent\":\"60\",\"DFIntElevatedPhysicsFPSReportThresholdTenths\":\"585\",\"DFIntExpireMarketPlaceServiceCacheSeconds\":\"60\",\"DFIntHttpCacheCleanMaxFilesToKeep\":\"7500\",\"DFIntHttpCacheCleanMinFilesRequired\":\"10000\",\"DFIntHttpInfluxHundredthsPercentage\":\"5\",\"DFIntHttpSendStatsEveryXSeconds\":\"300\",\"DFIntInfluxTattletaleCooldownSeconds\":\"300\",\"DFIntInfluxTattletaleInstancePathMaxLength\":\"200\",\"DFIntInfluxTattletalePerEventHundredthsPercent\":\"2000\",\"DFIntInfluxTattletalePerUserHundredthsPercent\":\"1\",\"DFIntInterpolationDelayFactorTenths\":\"10\",\"DFIntJoinInfluxHundredthsPercentage\":\"100\",\"DFIntLuaChatFloodCheckInterval\":\"15\",\"DFIntLuaChatFloodCheckMessages\":\"7\",\"DFIntMacInfluxHundredthsPercentage\":\"0\",\"DFIntMaxClusterKBPerSecond\":\"300\",\"DFIntMaxMissedWorldStepsRemembered\":\"16\",\"DFIntMoveInGameChatToTopPlaceId\":\"1\",\"DFIntNumPhysicsPacketsPerStep\":\"2\",\"DFIntPercentApiRequestsRecordGoogleAnalytics\":\"0\",\"DFIntReportDeviceInfoRate\":\"100\",\"DFIntSmoothTerrainPhysicsRayAabbSlop\":\"1\",\"DFIntStudioWorkspaceNotificationLevel\":\"0\",\"DFIntTargetTimeDelayFacctorTenths\":\"20\",\"DFIntTaskSchedularBatchErrorCalcFPS\":\"1200\",\"DFIntTeleportExceptionInfluxHundredthsPercentage\":\"9000\",\"DFIntThrottledVelocityThresholdTenths\":\"15\",\"DFIntUserHttpAccessUserId0\":\"0\",\"DFIntiOSInfluxHundredthsPercentage\":\"100\",\"DFLogLuaTypeErrors\":\"4\",\"DFStringHttpInfluxDatabase\":\"main\",\"DFStringHttpInfluxPassword\":\"password\",\"DFStringHttpInfluxURL\":\"https://www.projex.zip/\",\"DFStringHttpInfluxUser\":\"user\",\"DFStringHttpInfluxWriterPassword\":\"faster1Play\",\"DFStringRobloxAnalyticsURL\":\"https://www.projex.zip/\",\"EnableFullMonitorsResolution\":\"True\",\"ExcludeContactWithInteriorTerrainMinusYFace\":\"True\",\"FFLagEnableFullMonitorsResolution\":\"True\",\"FFlagAdServiceReportImpressions\":\"True\",\"FFlagAdvancedRCCLoadFMODAttemptReportDeviceInfoOnFailure\":\"True\",\"FFlagAdvancedRCCLoadFMODReportDeviceInfo\":\"True\",\"FFlagAdvancedRCCLoadFMODRetry\":\"True\",\"FFlagAllowCommentedScriptSigs\":\"True\",\"FFlagAllowCopyUnArchivableObjects\":\"True\",\"FFlagAllowFullColorSequences\":\"True\",\"FFlagAllowInsertConstrainedValuesAnywhere\":\"True\",\"FFlagAllowInsertFreeModels\":\"True\",\"FFlagAllowOutOfBoxAssets\":\"False\",\"FFlagAllowResizeRenderBufferiOS\":\"True\",\"FFlagAltSpinlock\":\"True\",\"FFlagAlternateFontKerning\":\"True\",\"FFlagAnalyzerGroupUIEnabled\":\"True\",\"FFlagArcHandlesBidirectional\":\"True\",\"FFlagAsyncPostMachineInfo\":\"True\",\"FFlagAutoJumpForTouchDevices\":\"True\",\"FFlagAutoRotateFlag\":\"True\",\"FFlagAutodetectCPU\":\"True\",\"FFlagBadTypeOnPcallEnabled\":\"True\",\"FFlagBalancingRateLimit\":\"True\",\"FFlagBetterPlaceLauncherStatusHandling\":\"True\",\"FFlagBetterSleepingJobErrorComputation\":\"True\",\"FFlagBetterTabManagement\":\"True\",\"FFlagBillboardGuiVR\":\"True\",\"FFlagBindPurchaseValidateCallbackInMarketplaceService\":\"True\",\"FFlagBlockBlockNarrowPhaseRefactor\":\"True\",\"FFlagBreakOnErrorConfirmationDialog\":\"True\",\"FFlagBubbleChatbarDocksAtTop\":\"True\",\"FFlagCSGAllowUnorderedProperties\":\"False\",\"FFlagCSGChangeHistory\":\"True\",\"FFlagCSGDataLossFixEnabled\":\"True\",\"FFlagCSGDecalsEnabled\":\"True\",\"FFlagCSGDecalsV2\":\"True\",\"FFlagCSGDelayParentingOperationToEnd\":\"True\",\"FFlagCSGDictionaryServiceEnabled\":\"True\",\"FFlagCSGLoadBlocking\":\"False\",\"FFlagCSGMeshColorEnable\":\"True\",\"FFlagCSGMeshColorToolsEnabled\":\"True\",\"FFlagCSGMeshRenderEnable\":\"True\",\"FFlagCSGMigrateChildData\":\"True\",\"FFlagCSGNewTriangulate\":\"True\",\"FFlagCSGPhysicsLevelOfDetailEnabled\":\"True\",\"FFlagCSGReportSuccessFailure\":\"True\",\"FFlagCSGScaleEnabled\":\"True\",\"FFlagCSGStripPublishedData\":\"True\",\"FFlagCSGToolsEnabled\":\"True\",\"FFlagCSGUnionCatchUnknownExceptions\":\"False\",\"FFlagCSGVoxelizer\":\"True\",\"FFlagCSGVoxelizerPrecompute\":\"False\",\"FFlagCallSetFocusFromCorrectThread\":\"True\",\"FFlagCameraChangeHistory\":\"True\",\"FFlagCameraInterpolateMethodEnhancement\":\"False\",\"FFlagCameraUseOwnViewport\":\"True\",\"FFlagCameraZoomNoModifier\":\"True\",\"FFlagCancelInputOnGuiNavigation\":\"True\",\"FFlagCancelPendingTextureLoads\":\"False\",\"FFlagChangeHistoryFixPendingChanges\":\"True\",\"FFlagChannelMasterMuting\":\"True\",\"FFlagCharAnimationStats\":\"False\",\"FFlagChatFilterWorksLocally\":\"True\",\"FFlagChatLayoutChange\":\"False\",\"FFlagChatServiceReplicates\":\"True\",\"FFlagChatVisiblePropertyEnabled\":\"True\",\"FFlagCheckDegenerateCases\":\"True\",\"FFlagCheckPlayerProcessMutexCreation\":\"True\",\"FFlagCheckRegisterSoundChannelUniqueness\":\"True\",\"FFlagCheckSleepOptimization\":\"True\",\"FFlagClampControllerVelocityMag\":\"True\",\"FFlagCleanFilteringEnabledLocalSpawnParts\":\"True\",\"FFlagClientABTestingEnabled\":\"False\",\"FFlagClientLoadFMODReportDeviceInfo\":\"True\",\"FFlagCloudEditDoNotLoadCoreScripts\":\"True\",\"FFlagCloudIconFixEnabled\":\"True\",\"FFlagCollectClientIDUpdateStatistics\":\"True\",\"FFlagConfigurableLineThickness\":\"True\",\"FFlagConsoleCodeExecutionEnabled\":\"True\",\"FFlagConstraintPropertyReplicationRaceConditionFixEnabled\":\"True\",\"FFlagConstraintUIEnabled\":\"True\",\"FFlagControllerMenu\":\"True\",\"FFlagCoreScriptShowVisibleAgeV2\":\"False\",\"FFlagCorescriptNewLoadChat\":\"True\",\"FFlagCrashOnScriptCloseFixEnabled\":\"True\",\"FFlagCreatePlaceEnabled\":\"True\",\"FFlagCreatePlaceInPlayerInventoryEnabled\":\"True\",\"FFlagCreateReplicatedStorageInStudio\":\"True\",\"FFlagCreateServerScriptServiceInStudio\":\"True\",\"FFlagCreateServerStorageInStudio\":\"True\",\"FFlagCustomEmitterLuaTypesEnabled\":\"True\",\"FFlagCustomEmitterRenderEnabled\":\"True\",\"FFlagCyclicExecutivePriorityJobs\":\"True\",\"FFlagCyclicExecutiveRenderJobRunsFirst\":\"True\",\"FFlagCylinderUsesConstantTessellation\":\"True\",\"FFlagD3D9CrashOnError\":\"False\",\"FFlagDE14316CrashFix\":\"True\",\"FFlagDE14317CrashFix\":\"True\",\"FFlagDE4423Fixed\":\"True\",\"FFlagDE4640Fixed\":\"True\",\"FFlagDE5511FixEnabled\":\"True\",\"FFlagDE7421Fixed\":\"True\",\"FFlagDE8768FixEnabled\":\"True\",\"FFlagDMFeatherweightEnabled\":\"True\",\"FFlagDataModelUseBinaryFormatForSave\":\"True\",\"FFlagDataStoreEnabled\":\"True\",\"FFlagDebugAdornableCrash\":\"True\",\"FFlagDebugCSGExportFailure\":\"False\",\"FFlagDebugCrashEnabled\":\"False\",\"FFlagDebugUseDefaultGlobalSettings\":\"True\",\"FFlagDefaultToFollowCameraOnTouch\":\"True\",\"FFlagDeferredContacts\":\"True\",\"FFlagDeleteLogsByDate\":\"True\",\"FFlagDeleteLogsOnMac\":\"True\",\"FFlagDep\":\"True\",\"FFlagDeprecateScriptInfoService\":\"True\",\"FFlagDetectTemplatesWhenSettingUpGameExplorerEnabled\":\"True\",\"FFlagDeviceEmulationStatistics\":\"True\",\"FFlagDirectX11Enable\":\"True\",\"FFlagDisableAbortRender\":\"True\",\"FFlagDisableBadUrl\":\"True\",\"FFlagDisableChangedServiceInTestMode\":\"True\",\"FFlagDisableLayersForNSViewOnEarlyMac\":\"True\",\"FFlagDisableScriptContextScriptsDisabled\":\"True\",\"FFlagDoIncrementalLoadingForR6AvatarFetch\":\"True\",\"FFlagDoNotPassSunkEventsToPlayerMouse\":\"True\",\"FFlagDontFireFakeMouseEventsOnUIS\":\"True\",\"FFlagDontSwallowInputForStudioShortcuts\":\"True\",\"FFlagDraggerCrashFixEnabled\":\"True\",\"FFlagDualSolverSimplex\":\"True\",\"FFlagDynamicEnvmapEnabled\":\"True\",\"FFlagEnableAdServiceVideoAds\":\"False\",\"FFlagEnableAndroidMenuLeave\":\"True\",\"FFlagEnableAnimationExport\":\"True\",\"FFlagEnableControllerGuiSelection\":\"True\",\"FFlagEnableDisplayDistances\":\"True\",\"FFlagEnableFullMonitorsResolution\":\"True\",\"FFlagEnableGetHitWhitelist\":\"True\",\"FFlagEnableLuaFollowers\":\"False\",\"FFlagEnableNonleathalExplosions\":\"True\",\"FFlagEnableRenderCSGTrianglesDebug\":\"False\",\"FFlagEnableRubberBandSelection\":\"True\",\"FFlagEnableSetCoreTopbarEnabled\":\"True\",\"FFlagEnableViewportScaling\":\"True\",\"FFlagEnableVoiceASR\":\"False\",\"FFlagEnableVoiceRecording\":\"False\",\"FFlagEnabledMouseIconStack\":\"True\",\"FFlagEnableiOSSettingsLeave\":\"True\",\"FFlagEnsureInputIsCorrectState\":\"False\",\"FFlagEntityNameEditingEnabled\":\"True\",\"FFlagExplosionsVisiblePropertyEnabled\":\"True\",\"FFlagFRMAdjustForMultiCore\":\"True\",\"FFlagFRMFixCullFlicker\":\"True\",\"FFlagFRMFogEnabled\":\"True\",\"FFlagFRMInStudio\":\"True\",\"FFlagFRMUse60FPSLockstepTable\":\"True\",\"FFlagFakePlayableDevices\":\"False\",\"FFlagFastClusterDisableReuse\":\"True\",\"FFlagFastClusterThrottleUpdateWaiting\":\"True\",\"FFlagFastFontMeasure\":\"False\",\"FFlagFastRevert\":\"True\",\"FFlagFastZlibPath\":\"True\",\"FFlagFeatureLvlsDX11BeforeDeviceCreate\":\"True\",\"FFlagFetchJoinScriptWithHttp\":\"False\",\"FFlagFilterAddSelectionToSameDataModel\":\"True\",\"FFlagFilterDoublePass\":\"False\",\"FFlagFilterEmoteChat\":\"True\",\"FFlagFilterMessageWithCallbackNoTryCatch\":\"True\",\"FFlagFilterSinglePass\":\"True\",\"FFlagFireUserInputServiceEventsAfterDMEvents\":\"True\",\"FFlagFixAlwaysOnTopSurfaceGuiInput\":\"True\",\"FFlagFixArraysNotUnmarkedFromCyclicTableDetection\":\"True\",\"FFlagFixBadMemoryOnStreamingGarbageCollection\":\"True\",\"FFlagFixBoxSelectWithCtrl\":\"True\",\"FFlagFixBulletGJKOptimization\":\"True\",\"FFlagFixCaptureFocusInput\":\"True\",\"FFlagFixCollisionFidelityTeamCreate\":\"True\",\"FFlagFixCorruptionInLogFiles\":\"True\",\"FFlagFixCrashAtShutdown\":\"True\",\"FFlagFixCrashOnEmptyTextOnAutoComplete\":\"True\",\"FFlagFixHumanoidRootPartCollision\":\"True\",\"FFlagFixIsCurrentlyVisibleSurfaceGuis\":\"True\",\"FFlagFixLoadingScreenAngle\":\"False\",\"FFlagFixLogCulling\":\"True\",\"FFlagFixLogManagerWritingToTempDir\":\"True\",\"FFlagFixMeshOffset\":\"True\",\"FFlagFixMouseFireOnEmulatingTouch\":\"True\",\"FFlagFixNoPhysicsGlitchWithGyro\":\"True\",\"FFlagFixPartOffset\":\"True\",\"FFlagFixPhysicalPropertiesComponentSet\":\"True\",\"FFlagFixPlayerProcessMutexDeadlock\":\"True\",\"FFlagFixPlayerProcessMutexDeadlockForReal\":\"True\",\"FFlagFixSlice9Scale\":\"True\",\"FFlagFixStickyDragBelowOrigin\":\"True\",\"FFlagFixStudioCursorJitter\":\"True\",\"FFlagFixStudioInGamePaste\":\"True\",\"FFlagFixSurfaceGuiGamepadNav\":\"True\",\"FFlagFixSurfaceGuiGazeSelect\":\"True\",\"FFlagFixTouchInputEventStates\":\"False\",\"FFlagFixUphillClimb\":\"True\",\"FFlagFixedStudioRotateTool\":\"True\",\"FFlagFlexibleTipping\":\"True\",\"FFlagFlyCamOnRenderStep\":\"True\",\"FFlagFontSizeUseLargest\":\"True\",\"FFlagFontSmoothScalling\":\"True\",\"FFlagFontSourceSans\":\"True\",\"FFlagFormFactorDeprecated\":\"False\",\"FFlagFramerateDeviationDroppedReport\":\"True\",\"FFlagFrustumTestGUI\":\"True\",\"FFlagGameConfigurerUseStatsService\":\"True\",\"FFlagGameExplorerAutofillImageNameFromFileName\":\"True\",\"FFlagGameExplorerBulkImageUpload\":\"True\",\"FFlagGameExplorerCopyPath\":\"True\",\"FFlagGameExplorerEnabled\":\"True\",\"FFlagGameExplorerImagesEnabled\":\"True\",\"FFlagGameExplorerImagesInsertEnabled\":\"True\",\"FFlagGameExplorerMoveImagesUnderAssetsGroup\":\"True\",\"FFlagGameExplorerPublishEnabled\":\"True\",\"FFlagGameExplorerUseV2AliasEndpoint\":\"True\",\"FFlagGameNameAtTopOfExplorer\":\"True\",\"FFlagGameNameLabelEnabled\":\"True\",\"FFlagGamepadCursorChanges\":\"True\",\"FFlagGetCorrectScreenResolutionFaster\":\"True\",\"FFlagGetUserIDFromPluginEnabled\":\"True\",\"FFlagGetUserIdFromPluginEnabled\":\"True\",\"FFlagGlowEnabled\":\"True\",\"FFlagGoogleAnalyticsTrackingEnabled\":\"True\",\"FFlagGradientStep\":\"True\",\"FFlagGraphicsD3D10\":\"True\",\"FFlagGraphicsD3D11HandleDeviceRemoved\":\"True\",\"FFlagGraphicsD3D11PickAdapter\":\"True\",\"FFlagGraphicsD3D9ComputeIndexRange\":\"True\",\"FFlagGraphicsD3DPointOne\":\"True\",\"FFlagGraphicsFeatureLvlStatsEnable\":\"True\",\"FFlagGraphicsGL3\":\"True\",\"FFlagGraphicsGLReduceLatency\":\"True\",\"FFlagGraphicsMacFix\":\"True\",\"FFlagGraphicsNoMainDepth\":\"True\",\"FFlagGraphicsTextureCommitChanges\":\"False\",\"FFlagGraphicsUseRetina\":\"True\",\"FFlagGuiRotationEnabled\":\"True\",\"FFlagHandleSoundPreviewWidgetWithNoSelectedSound\":\"True\",\"FFlagHeartbeatAt60Hz\":\"False\",\"FFlagHideDeprecatedEnums\":\"False\",\"FFlagHintsRenderInUserGuiRect\":\"True\",\"FFlagHttpCurlCacheHandles\":\"True\",\"FFlagHumanoidMoveToDefaultValueEnabled\":\"True\",\"FFlagHumanoidNetworkOptEnabled\":\"False\",\"FFlagHumanoidRenderBillboard\":\"True\",\"FFlagHumanoidStateInterfaces\":\"True\",\"FFlagHummanoidScaleEnable\":\"True\",\"FFlagImageRectEnabled\":\"True\",\"FFlagImprovedJoinScriptFlow\":\"True\",\"FFlagImprovedNameOcclusion\":\"True\",\"FFlagInformClientInsertFiltering\":\"True\",\"FFlagInitializeNewPlace\":\"True\",\"FFlagInsertUnderFolder\":\"True\",\"FFlagIntellisenseScriptContextDatamodelSearchingEnabled\":\"True\",\"FFlagInterpolationAwareTargetTime\":\"True\",\"FFlagInterpolationFix\":\"False\",\"FFlagInterpolationUseWightedDelay\":\"True\",\"FFlagJNIEnvScopeOptimization\":\"True\",\"FFlagKKTChecks\":\"False\",\"FFlagLadderCheckRate\":\"True\",\"FFlagLastWakeTimeSleepingJobError\":\"True\",\"FFlagLazyRenderingCoordinateFrame\":\"True\",\"FFlagLetLegacyScriptsWork\":\"True\",\"FFlagLimitHorizontalDragForce\":\"True\",\"FFlagLoadCharacterSoundFromCorescriptsRepo\":\"True\",\"FFlagLoadCommonModules\":\"True\",\"FFlagLoadCorescriptsPlatformDefMode\":\"True\",\"FFlagLoadLinkedScriptsOnDataModelLoad\":\"True\",\"FFlagLoadTimeModificationTestFlag\":\"True\",\"FFlagLocalMD5\":\"True\",\"FFlagLocalOptimizer\":\"True\",\"FFlagLogServiceEnabled\":\"True\",\"FFlagLowQMaterialsEnable\":\"True\",\"FFlagLuaBasedBubbleChat\":\"True\",\"FFlagLuaChatFiltering\":\"True\",\"FFlagLuaChatPhoneFontSize\":\"True\",\"FFlagLuaDebugProfileEnabled\":\"True\",\"FFlagLuaDebugger\":\"True\",\"FFlagLuaDebuggerBreakOnError\":\"True\",\"FFlagLuaDebuggerCloneDebugger\":\"True\",\"FFlagLuaDebuggerImprovedToolTip\":\"True\",\"FFlagLuaDebuggerNewCodeFlow\":\"True\",\"FFlagLuaDebuggerPopulateFuncName\":\"True\",\"FFlagLuaFollowers\":\"True\",\"FFlagLuaMathNoise\":\"True\",\"FFlagLuaUseBuiltinEqForEnum\":\"True\",\"FFlagMacInferredCrashReporting\":\"True\",\"FFlagMacRemoveUserInputJob\":\"True\",\"FFlagMaterialPropertiesEnabled\":\"True\",\"FFlagMaxFriendsCount\":\"True\",\"FFlagMeshPartMaterialTextureSupport\":\"True\",\"FFlagMessageOnLoadScriptValidationFail\":\"True\",\"FFlagMetaliOS\":\"True\",\"FFlagMinMaxDistanceEnabled\":\"True\",\"FFlagMobileToggleChatVisibleIcon\":\"True\",\"FFlagModelPluginsEnabled\":\"True\",\"FFlagModifyDefaultMaterialProperties\":\"True\",\"FFlagModuleScriptsPerVmEnabled\":\"False\",\"FFlagModuleScriptsPerVmEnabledFix2\":\"True\",\"FFlagModuleScriptsVisible\":\"True\",\"FFlagMouseCommandChangedSignalEnabled\":\"True\",\"FFlagMouseUseUserInputServiceMouse\":\"True\",\"FFlagMoveGameExplorerActionsIntoContextMenu\":\"True\",\"FFlagMutePreRollSoundService\":\"True\",\"FFlagNPSSetScriptDocsReadOnly\":\"True\",\"FFlagNativeSafeChatRendering\":\"True\",\"FFlagNetworkKeepItemPools\":\"True\",\"FFlagNetworkReplicateTerrainProperties\":\"True\",\"FFlagNewBackpackScript\":\"True\",\"FFlagNewBadgeServiceUrlEnabled\":\"True\",\"FFlagNewColor3Functions\":\"True\",\"FFlagNewCreatePlaceFlowEnabled\":\"True\",\"FFlagNewDefaultScriptSource\":\"True\",\"FFlagNewInGameDevConsole\":\"True\",\"FFlagNewIncomingPhysicsManagement\":\"True\",\"FFlagNewLayoutAndConstraintsEnabled\":\"True\",\"FFlagNewLightAPI\":\"True\",\"FFlagNewLoadingScreen\":\"True\",\"FFlagNewMenuSettingsScript\":\"True\",\"FFlagNewNotificationsScript\":\"True\",\"FFlagNewPlayerListScript\":\"True\",\"FFlagNewPurchaseScript\":\"True\",\"FFlagNewUniverseInfoEndpointEnabled\":\"True\",\"FFlagNewVehicleHud\":\"True\",\"FFlagNewWaterMaterialEnable\":\"True\",\"FFlagNoClimbPeople\":\"True\",\"FFlagNoCollideLadderFilter\":\"True\",\"FFlagNoWallClimb\":\"False\",\"FFlagOSXUseSDL\":\"False\",\"FFlagOnScreenProfiler\":\"True\",\"FFlagOnScreenProfilerGPU\":\"True\",\"FFlagOnlyProcessGestureEventsWhenSunk\":\"True\",\"FFlagOpenNewWindowsInDefaultBrowser\":\"True\",\"FFlagOpenScriptWorksOnModulesEnabled\":\"True\",\"FFlagOptimizeAnimatorCalcJoints\":\"True\",\"FFlagOptimizedDragger\":\"True\",\"FFlagOrderedDataStoreEnabled\":\"True\",\"FFlagOutlineControlEnabled\":\"True\",\"FFlagOverlayDataModelEnabled\":\"True\",\"FFlagOverrideTypeFunction\":\"True\",\"FFlagPGSApplyImpulsesAtMidpoints\":\"True\",\"FFlagPGSGlueJoint\":\"True\",\"FFlagPGSSolverBodyCacheLeakFix\":\"True\",\"FFlagPGSSolverDefaultOnNewPlaces\":\"True\",\"FFlagPGSSteppingMotorFix\":\"True\",\"FFlagPGSUsesConstraintBasedBodyMovers\":\"True\",\"FFlagPGSVariablePenetrationMargin\":\"False\",\"FFlagPGSVariablePenetrationMarginFix\":\"True\",\"FFlagParticleCullFix\":\"True\",\"FFlagPathfindingClientComputeEnabled\":\"True\",\"FFlagPerformanceStatsCollectionEnabled\":\"True\",\"FFlagPhysPropConstructFromMaterial\":\"True\",\"FFlagPhysics60HZ\":\"True\",\"FFlagPhysicsAllowAutoJointsWithSmallParts_DE6056\":\"True\",\"FFlagPhysicsAnalyzerEnabled\":\"True\",\"FFlagPhysicsBulletConnectorMatching\":\"True\",\"FFlagPhysicsBulletConnectorPointRecalc\":\"True\",\"FFlagPhysicsBulletUseProximityMatching\":\"False\",\"FFlagPhysicsCSGUsesBullet\":\"True\",\"FFlagPhysicsCylinders\":\"True\",\"FFlagPhysicsLockGroupDraggerHitPointOntoSurface_DE6174\":\"True\",\"FFlagPhysicsOptimizeSendClumpChanged\":\"True\",\"FFlagPhysicsPreventGroupDraggerPlacementToMinus400_DE6267\":\"True\",\"FFlagPhysicsRemoveWorldAssemble_US16512\":\"True\",\"FFlagPhysicsSkipRedundantJoinAll\":\"True\",\"FFlagPhysicsSkipUnnecessaryContactCreation\":\"False\",\"FFlagPhysicsUseKDTreeForCSG\":\"True\",\"FFlagPlaceLauncherThreadCheckDmClosed\":\"True\",\"FFlagPlaceLauncherUsePOST\":\"True\",\"FFlagPlayPauseFix\":\"True\",\"FFlagPlayerDropDownEnabled\":\"True\",\"FFlagPlayerHumanoidStep60Hz\":\"True\",\"FFlagPlayerMouseRespectGuiOffset\":\"True\",\"FFlagPlayerScriptsNotArchivable\":\"True\",\"FFlagPluginSaveSelection\":\"True\",\"FFlagPreventInterpolationOnCFrameChange\":\"True\",\"FFlagPrimalSolverLogBarrierIP\":\"True\",\"FFlagPrimalSolverSimplex\":\"True\",\"FFlagProcessAllPacketsPerStep\":\"True\",\"FFlagPromoteAssemblyModifications\":\"False\",\"FFlagQTStudioPublishFailure\":\"True\",\"FFlagQtAutoSave\":\"True\",\"FFlagQtFixToolDragging\":\"True\",\"FFlagQtPlaySoloOptimization\":\"True\",\"FFlagQtRightClickContextMenu\":\"True\",\"FFlagQtStudioScreenshotEnabled\":\"True\",\"FFlagRCCLoadFMOD\":\"True\",\"FFlagRCCSupportTeamTest\":\"True\",\"FFlagRDBGHashStringComparison\":\"True\",\"FFlagRakNetReadFast\":\"True\",\"FFlagRakNetSupportIpV6\":\"False\",\"FFlagRapidJSONEnabled\":\"True\",\"FFlagReadCoordinateFrameFast\":\"False\",\"FFlagReconstructAssetUrl\":\"True\",\"FFlagRecordForceStereo\":\"True\",\"FFlagRecordInGameDeaths\":\"False\",\"FFlagReloadAllImagesOnDataReload\":\"True\",\"FFlagRemoveInterpolationReciever\":\"True\",\"FFlagRemoveOldAnalyticsImplementation\":\"True\",\"FFlagRemoveOldCountersImplementation\":\"True\",\"FFlagRemoveSoundServiceSoundDisabledProperty\":\"True\",\"FFlagRemoveTintingWhenActiveIsFalseOnImageButton\":\"True\",\"FFlagRemoveUnusedPhysicsSenders\":\"True\",\"FFlagRenderAnisotropy\":\"True\",\"FFlagRenderBlobShadows\":\"True\",\"FFlagRenderCameraFocusFix\":\"True\",\"FFlagRenderCheckTextureContentProvider\":\"True\",\"FFlagRenderDownloadAssets\":\"True\",\"FFlagRenderFastClusterEverywhere\":\"True\",\"FFlagRenderFastResolve\":\"True\",\"FFlagRenderFeatherweightEnabled\":\"True\",\"FFlagRenderFeatherweightUseGeometryGenerator\":\"True\",\"FFlagRenderFixAnchoredLag\":\"True\",\"FFlagRenderFixCameraFocus\":\"False\",\"FFlagRenderFixFog\":\"True\",\"FFlagRenderFixGBufferLOD\":\"True\",\"FFlagRenderFixGuiOrder\":\"True\",\"FFlagRenderFixLightGridDirty\":\"True\",\"FFlagRenderGBufferEverywhere\":\"False\",\"FFlagRenderGLES2\":\"True\",\"FFlagRenderLightGridEnabled\":\"True\",\"FFlagRenderLightGridShadows\":\"True\",\"FFlagRenderLightGridShadowsSmooth\":\"True\",\"FFlagRenderLightgridInPerformEnable\":\"True\",\"FFlagRenderLoopExplicit\":\"True\",\"FFlagRenderLowLatencyLoop\":\"False\",\"FFlagRenderMaterialsOnMobile\":\"True\",\"FFlagRenderMeshReturnsCorrectly\":\"False\",\"FFlagRenderMoreFonts\":\"True\",\"FFlagRenderNew\":\"True\",\"FFlagRenderNewFonts\":\"True\",\"FFlagRenderNewMaterials\":\"True\",\"FFlagRenderNewMegaCluster\":\"True\",\"FFlagRenderNoDepthLast\":\"True\",\"FFlagRenderNoLegacy\":\"True\",\"FFlagRenderOpenGLForcePOTTextures\":\"True\",\"FFlagRenderSafeChat\":\"False\",\"FFlagRenderSoftParticles\":\"True\",\"FFlagRenderTextureCompositorUseBudgetForSize\":\"True\",\"FFlagRenderUserGuiIn3DSpace\":\"True\",\"FFlagRenderVR\":\"True\",\"FFlagRenderVRBBGUI\":\"True\",\"FFlagRenderWangTiles\":\"True\",\"FFlagReportInGameAssetSales\":\"True\",\"FFlagRequestServerStatsV2Enabled\":\"True\",\"FFlagResetMouseCursorOnToolUnequip\":\"True\",\"FFlagResizeGuiOnStep\":\"True\",\"FFlagResponsiveJump\":\"True\",\"FFlagRestoreScriptSourceWhenRedoingScriptCreation\":\"True\",\"FFlagRestrictSales\":\"True\",\"FFlagRetentionTrackingEnabled\":\"True\",\"FFlagRetryWhenCloudEditEnabledEndpointFails\":\"True\",\"FFlagRibbonBarEnabled\":\"True\",\"FFlagRibbonBarEnabledGA\":\"True\",\"FFlagRibbonPartInsertNotAllowedInModel\":\"True\",\"FFlagRollOffModeEnabled\":\"True\",\"FFlagRwxFailReport\":\"True\",\"FFlagSandboxHash\":\"True\",\"FFlagScaleExplosionLifetime\":\"True\",\"FFlagScreenGuisClipDescendants\":\"True\",\"FFlagScriptAnalyzerFixLocalScope\":\"True\",\"FFlagScriptAnalyzerPlaceholder\":\"True\",\"FFlagScriptContextSinglePendingThreadsQueue\":\"False\",\"FFlagScrollingFrameMouseUpFix\":\"True\",\"FFlagScrollingFrameOverridesButtonsOnTouch\":\"True\",\"FFlagSearchToolboxByDataModelSearchString\":\"True\",\"FFlagSecureReceiptsBackendEnabled\":\"True\",\"FFlagSecureReceiptsFrontendEnabled\":\"True\",\"FFlagSelectPartOnUndoRedo\":\"True\",\"FFlagSelectSpinlock\":\"True\",\"FFlagSendFilteredExceptionOnInferredStep\":\"True\",\"FFlagSendLegacyMachineConfigInfo\":\"False\",\"FFlagSerializeCurrentlyOpenPlaceWhenPublishingGame\":\"True\",\"FFlagServerSenderDontSendInterpolatedPhysics\":\"False\",\"FFlagSetCoreDisableChatBar\":\"True\",\"FFlagSetCoreMoveChat\":\"True\",\"FFlagSetDataModelUniverseIdAfterPublishing\":\"True\",\"FFlagSetPhysicsToLastRealStateWhenBecomingOwner\":\"True\",\"FFlagShowAlmostAllItemsInExplorer\":\"True\",\"FFlagShowCoreGUIInExplorer\":\"True\",\"FFlagShowStreamingEnabledProp\":\"True\",\"FFlagShowWebPlaceNameOnTabWhenOpeningFromWeb\":\"True\",\"FFlagSimplifyKeyboardInputPath\":\"False\",\"FFlagSinkActiveGuiObjectMouseEvents\":\"False\",\"FFlagSkipAdornIfWorldIsNull\":\"True\",\"FFlagSkipSilentAudioOps\":\"True\",\"FFlagSleepBeforeSpinlock\":\"True\",\"FFlagSmoothMouseLock\":\"False\",\"FFlagSmoothTerrain\":\"True\",\"FFlagSmoothTerrainClient\":\"True\",\"FFlagSmoothTerrainCountCellVolume\":\"True\",\"FFlagSmoothTerrainLODEnabled\":\"True\",\"FFlagSmoothTerrainLODFalseCoarseNeighbor\":\"True\",\"FFlagSmoothTerrainLODFixSeams\":\"True\",\"FFlagSmoothTerrainPacked\":\"True\",\"FFlagSoundChannelMaxDistanceStopFMODChannel\":\"True\",\"FFlagSoundChannelOnAncestorChangedUseGameLaunchIntent\":\"True\",\"FFlagSoundGroupsAndEffectsEnabled\":\"True\",\"FFlagSoundIgnoreReplicatorJoinDataItemCache\":\"True\",\"FFlagSoundServiceGameConfigurerConfigureRunServiceRun\":\"True\",\"FFlagSoundTypeCheck\":\"True\",\"FFlagSoundsRespectDelayedStop\":\"False\",\"FFlagSoundscapeReplicateChildren\":\"True\",\"FFlagSparseCheckFastFail\":\"True\",\"FFlagSpatialHashMoreRoots\":\"True\",\"FFlagSpecificUserdataTypeErrors\":\"True\",\"FFlagSpheresAllowedCustom\":\"True\",\"FFlagSpinlock\":\"True\",\"FFlagStackTraceLinks\":\"True\",\"FFlagStartWindowMaximizedDefault\":\"True\",\"FFlagStateSpecificAutoJump\":\"True\",\"FFlagStatusBarProgress\":\"True\",\"FFlagStopLoadingStockSounds\":\"True\",\"FFlagStopNoPhysicsStrafe\":\"True\",\"FFlagStudio3DGridUseAALines\":\"False\",\"FFlagStudioActActionsAsTools\":\"True\",\"FFlagStudioAddBackoffToNotificationsReconnects\":\"True\",\"FFlagStudioAddHelpInContextMenu\":\"True\",\"FFlagStudioAdvanceCookieExpirationBugFixEnabled\":\"True\",\"FFlagStudioAllowAudioSettings\":\"True\",\"FFlagStudioAllowFullColorSequences\":\"True\",\"FFlagStudioAllowSoundDraggingFromToolbox\":\"True\",\"FFlagStudioAlwaysSetActionEnabledState\":\"True\",\"FFlagStudioAnalyticsEnabled\":\"True\",\"FFlagStudioAnalyticsRefactoring\":\"True\",\"FFlagStudioAuthenticationCleanup\":\"True\",\"FFlagStudioAuthenticationFailureCounterEnabled\":\"True\",\"FFlagStudioBatchItemMapAddChild\":\"True\",\"FFlagStudioBreakOnInfiniteLoops\":\"True\",\"FFlagStudioBreakOnInfiniteLoopsThreadingFixEnabled\":\"True\",\"FFlagStudioBuildGui\":\"True\",\"FFlagStudioCSGAssets\":\"True\",\"FFlagStudioCSGRotationalFix\":\"True\",\"FFlagStudioCacheRecentAction\":\"True\",\"FFlagStudioCheckForUpgrade\":\"True\",\"FFlagStudioChildProcessCleanEnabled\":\"True\",\"FFlagStudioCollapsibleTutorials\":\"True\",\"FFlagStudioCommandLineSaveEditText\":\"True\",\"FFlagStudioConsistentGuiInitalisation\":\"True\",\"FFlagStudioContextualHelpEnabled\":\"True\",\"FFlagStudioCookieDesegregation\":\"True\",\"FFlagStudioCookieParsingDisabled\":\"False\",\"FFlagStudioCorrectForRetinaScreensOnEarlyMac\":\"True\",\"FFlagStudioCtrlTabDocSwitchEnabled\":\"True\",\"FFlagStudioDE11536FixEnabled\":\"True\",\"FFlagStudioDE7928FixEnabled\":\"True\",\"FFlagStudioDE9108FixEnabled\":\"True\",\"FFlagStudioDE9132FixEnabled\":\"True\",\"FFlagStudioDE9818FixEnabled\":\"True\",\"FFlagStudioDataModelIsStudioFix\":\"True\",\"FFlagStudioDeadCodeOnMouseDown\":\"True\",\"FFlagStudioDebuggerVisitDescendants\":\"True\",\"FFlagStudioDecalPasteFix\":\"True\",\"FFlagStudioDefaultWidgetSizeChangesEnabled\":\"True\",\"FFlagStudioDeviceEmulationTouchInputFix\":\"True\",\"FFlagStudioDisableEditingCurrentEditor\":\"True\",\"FFlagStudioDisableScrollingOnEarlyMac\":\"True\",\"FFlagStudioDraggerCrashFixEnabled\":\"True\",\"FFlagStudioDraggerFixes\":\"True\",\"FFlagStudioDraggersScaleFixes\":\"True\",\"FFlagStudioDuplicateActionEnabled\":\"True\",\"FFlagStudioEmbeddedFindDialogEnabled\":\"True\",\"FFlagStudioEnableDebuggerPerfImprovements\":\"True\",\"FFlagStudioEnableGameAnimationsTab\":\"True\",\"FFlagStudioEnableGamepadSupport\":\"True\",\"FFlagStudioEnableLayersForNSView\":\"True\",\"FFlagStudioEnableWebKitPlugins\":\"True\",\"FFlagStudioExplorerActionsEnabledInScriptView\":\"True\",\"FFlagStudioExplorerDisabledByDefault\":\"True\",\"FFlagStudioFindCrashFixEnabled\":\"True\",\"FFlagStudioFindInAllScriptsEnabled\":\"True\",\"FFlagStudioFireStickyMouseCommandChangedOnly\":\"True\",\"FFlagStudioFixLockingScriptDisablesMenuOptions\":\"True\",\"FFlagStudioFixPauseDuringLoad\":\"True\",\"FFlagStudioFixPropertiesWindowScrollBarNotShowing\":\"True\",\"FFlagStudioFixTestApis\":\"True\",\"FFlagStudioFixToolboxReload\":\"True\",\"FFlagStudioFixUndeletingSoundCausesPlayback\":\"True\",\"FFlagStudioFlycamAppBridgeFix\":\"True\",\"FFlagStudioForceToolboxSize\":\"True\",\"FFlagStudioHiddenPropertyCrashFixEnabled\":\"True\",\"FFlagStudioHideInsertedServices\":\"True\",\"FFlagStudioHideMouseCoursorOnCommand\":\"True\",\"FFlagStudioHomeKeyChangeEnabled\":\"True\",\"FFlagStudioIgnoreMouseMoveOnIdle\":\"True\",\"FFlagStudioIgnoreSSLErrors\":\"True\",\"FFlagStudioImproveModelDragFidelity\":\"True\",\"FFlagStudioInSyncWebKitAuthentication\":\"False\",\"FFlagStudioIncreasedBaseplateSize\":\"True\",\"FFlagStudioInitializeViewOnPaint\":\"True\",\"FFlagStudioInsertAtMouseClick\":\"True\",\"FFlagStudioInsertAtTopCenterOfSelection\":\"True\",\"FFlagStudioInsertIntoStarterPack\":\"True\",\"FFlagStudioInsertModelCounterEnabled\":\"True\",\"FFlagStudioInsertOrientationFix\":\"True\",\"FFlagStudioIntellesenseEnabled\":\"True\",\"FFlagStudioIntellesenseOnAllMembersEnabled\":\"True\",\"FFlagStudioKeyboardMouseConfig\":\"True\",\"FFlagStudioLaunchDecalToolAfterDrag\":\"True\",\"FFlagStudioLightGridAPIVisible\":\"True\",\"FFlagStudioLightGridOnForNewPlaces\":\"True\",\"FFlagStudioLiveCoding\":\"True\",\"FFlagStudioLoadPluginsLate\":\"True\",\"FFlagStudioLocalSpaceDragger\":\"True\",\"FFlagStudioLockScriptsWithoutBlocking\":\"True\",\"FFlagStudioLockServiceParents\":\"True\",\"FFlagStudioLuaDebugger\":\"True\",\"FFlagStudioLuaDebuggerGA\":\"True\",\"FFlagStudioMacAddressValidationEnabled\":\"True\",\"FFlagStudioMiddleMouseTrackCamera\":\"False\",\"FFlagStudioMockPurchasesEnabled\":\"True\",\"FFlagStudioModuleScriptDefaultContents\":\"True\",\"FFlagStudioMouseOffsetFixEnabled\":\"True\",\"FFlagStudioNativeKeepSavedChanges\":\"True\",\"FFlagStudioNewFonts\":\"True\",\"FFlagStudioNewWiki\":\"True\",\"FFlagStudioOneClickColorPickerEnabled\":\"True\",\"FFlagStudioOneStudGridDefault\":\"True\",\"FFlagStudioOnlyOneToolboxPreviewAtATime\":\"True\",\"FFlagStudioOnlyUpdateTeamTestActionsIfChanged\":\"True\",\"FFlagStudioOpenLastSaved\":\"False\",\"FFlagStudioOpenStartPageForLogin\":\"True\",\"FFlagStudioOrthonormalizeSafeRotate\":\"True\",\"FFlagStudioPartAlignmentChangeEnabled\":\"True\",\"FFlagStudioPartSymmetricByDefault\":\"True\",\"FFlagStudioPasteAsSiblingEnabled\":\"True\",\"FFlagStudioPasteSyncEnabled\":\"True\",\"FFlagStudioPlaceAssetFromToolbox\":\"True\",\"FFlagStudioPlaceOnlineIndicator\":\"True\",\"FFlagStudioPlaySoloCharacterAutoLoadsNullTool\":\"True\",\"FFlagStudioPlaySoloConfigurerLegacyPlayerName\":\"True\",\"FFlagStudioPlaySoloMapDebuggerData\":\"True\",\"FFlagStudioPluginUIActionEnabled\":\"True\",\"FFlagStudioProgressIndicatorForInsertEnabled\":\"True\",\"FFlagStudioPromptWhenInsertingConstraints\":\"True\",\"FFlagStudioPropertiesRespectCollisionToggle\":\"True\",\"FFlagStudioPropertyChangedSignalHandlerFix\":\"True\",\"FFlagStudioPropertyErrorOutput\":\"True\",\"FFlagStudioPropertySliderEnabled\":\"True\",\"FFlagStudioPropertyWidgetRemoveUpdateEvents\":\"True\",\"FFlagStudioPublishToRobloxActionUXAlwaysAvailable\":\"True\",\"FFlagStudioPushTreeWidgetUpdatesToMainThread\":\"True\",\"FFlagStudioQuickAccessCustomization\":\"True\",\"FFlagStudioQuickInsertEnabled\":\"True\",\"FFlagStudioRecentSavesEnabled\":\"True\",\"FFlagStudioReduceTeamCreateStuttering\":\"True\",\"FFlagStudioRelocateSoundJob\":\"True\",\"FFlagStudioRemoveDebuggerResumeLock\":\"True\",\"FFlagStudioRemoveDuplicateParts\":\"True\",\"FFlagStudioRemoveToolSounds\":\"True\",\"FFlagStudioRemoveUpdateUIThread\":\"True\",\"FFlagStudioRenderRemotePlayerSelection\":\"True\",\"FFlagStudioReopenClosedTabsShortcut\":\"True\",\"FFlagStudioReportCachedRecentActions\":\"True\",\"FFlagStudioReportVitalParameters\":\"True\",\"FFlagStudioResizeMeshPartOnImport\":\"True\",\"FFlagStudioRespectMeshOffset\":\"False\",\"FFlagStudioRibbonBarLayoutFixes\":\"True\",\"FFlagStudioRibbonBarNewLayout\":\"True\",\"FFlagStudioRibbonGroupResizeFixEnabled\":\"True\",\"FFlagStudioSanitizeInstancesOnLoad\":\"True\",\"FFlagStudioScriptAnalysisGetOrCreateRefactoring\":\"True\",\"FFlagStudioScriptBlockAutocomplete\":\"True\",\"FFlagStudioSendMouseIdleToPluginMouse\":\"True\",\"FFlagStudioSeparateActionByActivationMethod\":\"False\",\"FFlagStudioSetObjectsFromPropertiesWindow\":\"True\",\"FFlagStudioSetViewportSizeOfClone\":\"True\",\"FFlagStudioShowNotSavingScriptEditsOnce\":\"True\",\"FFlagStudioShowTutorialsByDefault\":\"True\",\"FFlagStudioSmoothTerrainForNewPlaces\":\"True\",\"FFlagStudioSmoothTerrainPlugin\":\"True\",\"FFlagStudioSpawnLocationsDefaultValues\":\"True\",\"FFlagStudioStopSoundPlaybackAfterRemoval\":\"True\",\"FFlagStudioSupportBytecodeDeserialize\":\"True\",\"FFlagStudioTabOrderingEnabled\":\"True\",\"FFlagStudioTeamCreateWebChatBackendEnabled\":\"True\",\"FFlagStudioTeleportPlaySolo\":\"True\",\"FFlagStudioToolBoxInsertUseRayTrace\":\"True\",\"FFlagStudioToolBoxModelDragFix\":\"True\",\"FFlagStudioToolboxModelDragToCastPoint\":\"True\",\"FFlagStudioTreeWidgetCheckDeletingFlagWhenDoingUpdates\":\"True\",\"FFlagStudioTreeWidgetPotentialMemoryLeak\":\"True\",\"FFlagStudioTrimPropertyWhitespace\":\"True\",\"FFlagStudioTutorialSeeAll\":\"True\",\"FFlagStudioUndoEnabledForEdit\":\"True\",\"FFlagStudioUpdatePropertiesWithoutJob\":\"True\",\"FFlagStudioUpdateRestoreBehavior\":\"True\",\"FFlagStudioUpdateSAResultsInUIThread\":\"True\",\"FFlagStudioUseBinaryFormatForModelPublish\":\"True\",\"FFlagStudioUseBinaryFormatForModelSave\":\"True\",\"FFlagStudioUseBinaryFormatForPlay\":\"True\",\"FFlagStudioUseBinaryFormatForSave\":\"True\",\"FFlagStudioUseDelayedSyntaxCheck\":\"True\",\"FFlagStudioUseExtendedHTTPTimeout\":\"True\",\"FFlagStudioUseHttpAuthentication\":\"True\",\"FFlagStudioUseHttpsForUserInfo\":\"True\",\"FFlagStudioUseMarketplaceApiClient\":\"True\",\"FFlagStudioUsePlaySoloConfigurer\":\"True\",\"FFlagStudioUseScriptAnalyzer\":\"True\",\"FFlagStudioUseServerConfigurer\":\"True\",\"FFlagStudioUserNotificationIgnoreSequenceNumber\":\"True\",\"FFlagStudioValidateBootstrapper\":\"True\",\"FFlagStudioVariableIntellesense\":\"True\",\"FFlagStudioVideoRecordFix\":\"True\",\"FFlagStudioZoomExtentsExplorerFixEnabled\":\"True\",\"FFlagSubmitEditedColor3WhenFocusLost\":\"True\",\"FFlagSuppressNavOnTextBoxFocus\":\"False\",\"FFlagSupressNavOnTextBoxFocus\":\"True\",\"FFlagSurfaceGuiObjectEnabledCheck\":\"True\",\"FFlagSurfaceGuiVisible\":\"True\",\"FFlagSurfaceLightEnabled\":\"True\",\"FFlagSymmetricContact\":\"True\",\"FFlagSyncRenderingAndPhysicsInterpolation\":\"True\",\"FFlagTaskSchedulerCyclicExecutive\":\"True\",\"FFlagTaskSchedulerCyclicExecutiveStudio\":\"True\",\"FFlagTattletaleFixTextValue\":\"True\",\"FFlagTeamCreateOptimizeRemoteSelection\":\"True\",\"FFlagTeleportDetailedInfluxHttpStatusError\":\"True\",\"FFlagTerrainLazyGrid\":\"True\",\"FFlagTerrainOptimizedCHS\":\"True\",\"FFlagTerrainOptimizedLoad\":\"True\",\"FFlagTerrainOptimizedStorage\":\"True\",\"FFlagTestMenuEnabledOnAllWindows\":\"True\",\"FFlagTextBoundRespectTextScaled\":\"True\",\"FFlagTextBoxUnicodeAware\":\"True\",\"FFlagTextFieldUTF8\":\"True\",\"FFlagTexturePropertyWidgetEnabled\":\"True\",\"FFlagToggleDevConsoleThroughChatCommandEnabled\":\"True\",\"FFlagTouchTransmitterWeakPtr\":\"True\",\"FFlagTrackModuleScripts\":\"True\",\"FFlagTrackOriginalClientID\":\"True\",\"FFlagTreatCloudEditAsEditGameMode\":\"True\",\"FFlagTurnOffiOSNativeControls\":\"True\",\"FFlagTweenCallbacksDuringRenderStep\":\"True\",\"FFlagTweenServiceUsesRenderStep\":\"True\",\"FFlagTypesettersReleaseResources\":\"True\",\"FFlagUDim2LerpEnabled\":\"True\",\"FFlagUS14116\":\"True\",\"FFlagUpdatePrimitiveStateForceSleep\":\"True\",\"FFlagUseAvatarFetchAPI\":\"True\",\"FFlagUseAvatarFetchThumbnailLogic\":\"True\",\"FFlagUseAveragedFloorHeight\":\"True\",\"FFlagUseBuildGenericGameUrl\":\"True\",\"FFlagUseCameraOffset\":\"True\",\"FFlagUseCanManageApiToDetermineConsoleAccess\":\"False\",\"FFlagUseCommonModules\":\"True\",\"FFlagUseCorrectDoppler\":\"True\",\"FFlagUseDynamicTypesetterUTF8\":\"True\",\"FFlagUseFollowCamera\":\"True\",\"FFlagUseGameAvatarTypeEnum\":\"True\",\"FFlagUseHttpsForGameserverAshx\":\"True\",\"FFlagUseInGameTopBar\":\"True\",\"FFlagUseLegacyEnumItemLookup\":\"True\",\"FFlagUseMinMaxZoomDistance\":\"True\",\"FFlagUseNewAppBridgeAndroid\":\"True\",\"FFlagUseNewAppBridgeIOS\":\"True\",\"FFlagUseNewAppBridgeInputWindows\":\"False\",\"FFlagUseNewAppBridgeOSX\":\"True\",\"FFlagUseNewAppBridgeStudio\":\"True\",\"FFlagUseNewAppBridgeWindows\":\"True\",\"FFlagUseNewBadgesCreatePage\":\"True\",\"FFlagUseNewCameraZoomPath\":\"True\",\"FFlagUseNewContentProvider\":\"False\",\"FFlagUseNewHumanoidCache\":\"True\",\"FFlagUseNewKeyboardHandling\":\"True\",\"FFlagUseNewNotificationPathLua\":\"True\",\"FFlagUseNewPromptEndHandling\":\"True\",\"FFlagUseNewSoftwareMouseRender\":\"True\",\"FFlagUseNewSoundEngine\":\"True\",\"FFlagUseNewSoundEngine3dFix\":\"True\",\"FFlagUseNewSubdomainsInCoreScripts\":\"True\",\"FFlagUseNewXboxLoginFlow\":\"True\",\"FFlagUsePGSSolver\":\"True\",\"FFlagUsePreferredSoundDevice\":\"True\",\"FFlagUseShortShingles\":\"True\",\"FFlagUseSimpleRapidJson\":\"True\",\"FFlagUseStrongerBalancer\":\"True\",\"FFlagUseToStringN\":\"True\",\"FFlagUseTopmostSettingToBringWindowToFront\":\"True\",\"FFlagUseUniverseGetInfoCallToDetermineUniverseAccess\":\"True\",\"FFlagUseUpdatedKeyboardSettings\":\"False\",\"FFlagUseUpdatedSyntaxHighlighting\":\"True\",\"FFlagUseUserListMenu\":\"True\",\"FFlagUseVRKeyboardInLua\":\"True\",\"FFlagUseWindowSizeFromGameSettings\":\"True\",\"FFlagUserAllCamerasInLua\":\"False\",\"FFlagUserHttpAPIVisible\":\"True\",\"FFlagUserInputServicePipelineMacClient\":\"True\",\"FFlagUserInputServicePipelineStudio\":\"True\",\"FFlagUserInputServicePipelineWindowsClient\":\"True\",\"FFlagUserJumpButtonPositionChange\":\"True\",\"FFlagUserUseNewControlScript\":\"True\",\"FFlagVectorErrorOnNilArithmetic\":\"True\",\"FFlagVerifyConnection\":\"True\",\"FFlagVideoDocumentationPluginEnabled\":\"True\",\"FFlagVolumeControlInGameEnabled\":\"True\",\"FFlagVoxelCompressedStorage\":\"True\",\"FFlagWaitForChildTimeOut\":\"True\",\"FFlagWarnForLegacyTerrain\":\"True\",\"FFlagWaterParams\":\"True\",\"FFlagWedgeEnableDecalOnTop\":\"True\",\"FFlagWikiSelectionSearch\":\"True\",\"FFlagWindowsInferredCrashReporting\":\"True\",\"FFlagWindowsNoDmpRetry\":\"False\",\"FFlagWindowsUseSDL\":\"False\",\"FFlagWorkspaceLoadStringEnabledHidden\":\"True\",\"FIntAvatarEditorAndroidRollout\":\"1\",\"FIntEnableAvatarEditorAndroid\":\"1\",\"FIntEnableAvatarEditoriOS\":\"100\",\"FIntGamePerfMonitorPercentage\":\"10\",\"FIntLaunchInfluxHundredthsPercentage\":\"0\",\"FIntLuaAssertCrash\":\"0\",\"FIntMinMillisecondLengthForLongSoundChannel\":\"5000\",\"FIntNumSmoothingPasses\":\"3\",\"FIntPGSPentrationMarginMax\":\"50000\",\"FIntPhysicalPropDensity_ALUMINUM_MATERIAL\":\"2700\",\"FIntPhysicalPropElasticity_ALUMINUM_MATERIAL\":\"250\",\"FIntPhysicalPropElasticity_BASALT_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_BRICK_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_COBBLESTONE_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_CRACKED_LAVA_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_DIAMONDPLATE_MATERIAL\":\"250\",\"FIntPhysicalPropElasticity_FABRIC_MATERIAL\":\"50\",\"FIntPhysicalPropElasticity_GLACIER_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_GRASS_MATERIAL\":\"100\",\"FIntPhysicalPropElasticity_GROUND_MATERIAL\":\"100\",\"FIntPhysicalPropElasticity_ICE_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_MARBLE_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_METAL_MATERIAL\":\"250\",\"FIntPhysicalPropElasticity_MUD_MATERIAL\":\"70\",\"FIntPhysicalPropElasticity_PEBBLE_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_ROCK_MATERIAL\":\"170\",\"FIntPhysicalPropElasticity_RUST_MATERIAL\":\"200\",\"FIntPhysicalPropElasticity_SANDSTONE_MATERIAL\":\"150\",\"FIntPhysicalPropElasticity_SAND_MATERIAL\":\"50\",\"FIntPhysicalPropElasticity_SNOW_MATERIAL\":\"30\",\"FIntPhysicalPropElasticity_WOODPLANKS_MATERIAL\":\"200\",\"FIntPhysicalPropElasticity_WOOD_MATERIAL\":\"200\",\"FIntPhysicalPropFWeight_BASALT_MATERIAL\":\"300\",\"FIntPhysicalPropFWeight_BRICK_MATERIAL\":\"300\",\"FIntPhysicalPropFWeight_CONCRETE_MATERIAL\":\"300\",\"FIntPhysicalPropFWeight_ICE_MATERIAL\":\"3000\",\"FIntPhysicalPropFWeight_SANDSTONE_MATERIAL\":\"5000\",\"FIntPhysicalPropFWeight_SAND_MATERIAL\":\"5000\",\"FIntPhysicalPropFriction_ALUMINUM_MATERIAL\":\"400\",\"FIntPhysicalPropFriction_BRICK_MATERIAL\":\"800\",\"FIntPhysicalPropFriction_CONCRETE_MATERIAL\":\"700\",\"FIntPhysicalPropFriction_DIAMONDPLATE_MATERIAL\":\"350\",\"FIntPhysicalPropFriction_NEON_MATERIAL\":\"300\",\"FIntPhysicalPropFriction_PLASTIC_MATERIAL\":\"300\",\"FIntPhysicalPropFriction_SANDSTONE_MATERIAL\":\"500\",\"FIntPhysicalPropFriction_SAND_MATERIAL\":\"500\",\"FIntPhysicalPropFriction_SMOOTH_PLASTIC_MATERIAL\":\"200\",\"FIntPhysicalPropFriction_SNOW_MATERIAL\":\"300\",\"FIntRegLambda\":\"1400\",\"FIntRenderGBufferMinQLvl\":\"16\",\"FIntRenderNewPercentMac\":\"100\",\"FIntRenderNewPercentWin\":\"100\",\"FIntStartupInfluxHundredthsPercentage\":\"100\",\"FIntStudioInsertDeletionCheckTimeMS\":\"30000\",\"FIntValidateLauncherPercent\":\"0\",\"FLogAsserts\":\"0\",\"FLogBrowserActivity\":\"3\",\"FLogCloseDataModel\":\"3\",\"FLogDXVideoMemory\":\"4\",\"FLogFullRenderObjects\":\"0\",\"FLogGraphics\":\"6\",\"FLogHangDetection\":\"3\",\"FLogLuaAssert\":\"0\",\"FLogLuaBridge\":\"2\",\"FLogNetworkItemQueueDtor\":\"1\",\"FLogNetworkPacketsReceive\":\"5\",\"FLogOutlineBrightnessMax\":\"160\",\"FLogOutlineBrightnessMin\":\"50\",\"FLogOutlineThickness\":\"40\",\"FLogPlayerShutdownLuaTimeoutSeconds\":\"1\",\"FLogServiceCreation\":\"4\",\"FLogServiceVectorResize\":\"4\",\"FLogStepAnimatedJoints\":\"5\",\"FLogUseLuaMemoryPool\":\"0\",\"FLogVR\":\"6\",\"FStringClientInsertFilterMoreInfoUrl\":\"http://devforum.roblox.com/t/coming-changes-to-insert-service/30327\",\"FStringPlaceFilter_InterpolationAwareTargetTime\":\"True;249779150;333368740;444708274;64542766;248207867;171391948;360589910;388599755;163865146;127243303;162537373;6597705;332248116;348681325;196235086;13822889;189707\",\"FStringPlaceFilter_NewLayoutAndConstraintsEnabled\":\"True;534842009;20213796;379132413;485971234;515782100;248207867;360699282;498699944;540764930;534808604;520456996;552894983;551169796;560164377;599021441;609763195;609918169;599392478;614429353;337448601;615210477;606827239;19481228;19827397;26953764;561540866;20397851;626302497;402593749;589006000;461274216;129419469;478459751;460710135;464914388;481987774;610775332;567211827;636396993\",\"FStringPlaceFilter_SetPhysicsToLastRealStateWhenBecomingOwner\":\"True;13822889;189707\",\"FStringStudioTutorialsTOCUrl\":\"http://wiki.roblox.com/index.php?title=Studio_Tutorials_Landing&studiomode=true\",\"FStringStudioTutorialsUrl\":\"http://wiki.roblox.com/index.php?title=Studio_Tutorials_Test&studiomode=true\",\"GoogleAnalyticsAccountPropertyID\":\"UA-43420590-3\",\"GoogleAnalyticsAccountPropertyIDClient\":\"\",\"GoogleAnalyticsAccountPropertyIDPlayer\":\"UA-43420590-14\",\"GoogleAnalyticsLoadStudio\":\"1\",\"HttpUseCurlPercentageMacClient\":\"100\",\"HttpUseCurlPercentageMacStudio\":\"100\",\"HttpUseCurlPercentageWinClient\":\"100\",\"HttpUseCurlPercentageWinStudio\":\"100\",\"MinNumberScriptExecutionsToGetPrize\":\"500\",\"PrizeAssetIDs\":\"\",\"PrizeAwarderURL\":\"/ostara/boon\",\"PublishedProjectsPageHeight\":\"535\",\"PublishedProjectsPageUrl\":\"/ide/publish\",\"PublishedProjectsPageWidth\":\"950\",\"SFFlagAllowPhysicsPacketCompression\":\"False\",\"SFFlagBinaryStringReplicationFix\":\"True\",\"SFFlagEquipToolOnClient\":\"True\",\"SFFlagGuid64Bit\":\"False\",\"SFFlagInfiniteTerrain\":\"True\",\"SFFlagMaterialPropertiesNewIsDefault\":\"True\",\"SFFlagNetworkStreamRotationAsFloat\":\"False\",\"SFFlagNetworkUseServerScope\":\"True\",\"SFFlagNewPhysicalPropertiesForcedOnAll\":\"True\",\"SFFlagOneWaySimRadiusReplication\":\"True\",\"SFFlagPathBasedPartMovement\":\"True\",\"SFFlagPhysicsPacketSendWorldStepTimestamp\":\"True\",\"SFFlagPreventInterpolationOnCFrameChange\":\"True\",\"SFFlagProtocolSynchronization\":\"True\",\"SFFlagR15CompositingEnabled\":\"True\",\"SFFlagReplicatedFirstEnabled\":\"True\",\"SFFlagSoundChannelUseV2Implementation\":\"True\",\"SFFlagStateBasedAnimationReplication\":\"True\",\"SFFlagTopRepContSync\":\"True\",\"SFFlagUseNewSetFMOD3D\":\"True\",\"StartPageUrl\":\"/ide/welcome\",\"VideoPreRollWaitTimeSeconds\":\"45\"}";
        }
        [HttpGet("/auth/submit")]
        public MVC.RedirectResult SubmitAuth(string auth)
        {
            return new MVC.RedirectResult("/");
        }

        [HttpGet("Game/PlaceLauncher.ashx")]
        public async Task<dynamic> RequestGame([Required, MVC.FromQuery] string request, long placeId, bool isPartyLeader, string gender, bool isTeleport)
        {
            FeatureFlags.FeatureCheck(FeatureFlag.GamesEnabled, FeatureFlag.GameJoinEnabled);
            var ip = GetIP();
            var ticket = services.gameServer.CreateTicket(userSession.userId, placeId, ip);
            var numericalID = Convert.ToInt64(placeId);
            var result = await services.gameServer.GetServerForPlace(numericalID);
            if (result.status == JoinStatus.Joining)
            {
                await Roblox.Metrics.GameMetrics.ReportGameJoinPlaceLauncherReturned(numericalID);
                return new
                {
                    jobId = result.job,
                    status = (int)result.status,
                    joinScriptUrl = Configuration.BaseUrl + "/Game/join.ashx?ticket=" + HttpUtility.UrlEncode(ticket) +
                                    "&job=" + HttpUtility.UrlEncode(result.job),
                    authenticationUrl = Configuration.BaseUrl + "/Login/Negotiate.ashx",
                    authenticationTicket = ticket,
                    message = (string?)null,
                };
            }

            return new
            {
                jobId = (string?)null,
                status = (int)result.status,
                message = "Waiting for server",
            };
        }

        [HttpGetBypass("placelauncher.ashx")]
        [MVC.HttpPost("placelauncher.ashx")]
        public async Task<dynamic> LaunchGame([Required, MVC.FromQuery] string ticket)
        {
            FeatureFlags.FeatureCheck(FeatureFlag.GamesEnabled, FeatureFlag.GameJoinEnabled);
            var ip = GetIP();
            var details = services.gameServer.DecodeTicket(ticket, ip);
            var result = await services.gameServer.GetServerForPlace(details.placeId);
            if (result.status == JoinStatus.Joining)
            {
                await Roblox.Metrics.GameMetrics.ReportGameJoinPlaceLauncherReturned(details.placeId);
                return new
                {
                    jobId = result.job,
                    status = (int)result.status,
                    joinScriptUrl = Configuration.BaseUrl + "/Game/join.ashx?ticket=" + HttpUtility.UrlEncode(ticket) +
                                    "&job=" + HttpUtility.UrlEncode(result.job),
                    authenticationUrl = Configuration.BaseUrl + "/Login/Negotiate.ashx",
                    authenticationTicket = ticket,
                    message = (string?)null,
                };
            }

            return new
            {
                jobId = (string?)null,
                status = (int)result.status,
                message = "Waiting for server",
            };
        }

        public static long startUserId { get; set; } = 30;
#if DEBUG
        [HttpGetBypass("/game/get-join-script-debug")]
        public async Task<dynamic> GetJoinScriptDebug(long placeId, long userId = 12)
        {
            //startUserId = 12;
            var result = services.gameServer.CreateTicket(startUserId, placeId, GetIP());
            startUserId++;
            return new
            {
                placeLauncher = $"{Configuration.BaseUrl}/placelauncher.ashx?ticket={HttpUtility.UrlEncode(result)}",
                authenticationTicket = result,
            };
        }
#endif

        /*[HttpGetBypass("users/{userId:long}/canmanage/{placeId:long}")]
        public async Task<dynamic> GetCanManage(long userId, long placeId)
        {
            var canEdit = await services.assets.CanUserModifyItem(placeId, userId);
            return new
            {
                Success = true,
                CanManage = canEdit,
            };
        }*/

        [HttpGetBypass("game/join.ashx")]
        public async Task<dynamic> JoinGame([Required, MVC.FromQuery] string ticket, [Required, MVC.FromQuery] string job)
        {
            FeatureFlags.FeatureCheck(FeatureFlag.GamesEnabled, FeatureFlag.GameJoinEnabled);
            var clientIp = GetIP();
            var ticketData = services.gameServer.DecodeTicket(ticket, clientIp);
            var serverData = services.gameServer.DecodeGameServerTicket(job);
            var userId = ticketData.userId;
            var userInfo = await services.users.GetUserById(userId);
            var accountAgeDays = DateTime.UtcNow.Subtract(userInfo.created).Days;
            var statusIs18OrOver = await services.users.Is18Plus(userId);
            var serverAddress = serverData.domain;
            // Completely random. IP and Port don't serve a purpose anymore, other than to make the client code happy.
            var ip = "85.215.209.73";
            var port = "53640";
#if DEBUG
            serverAddress = "localhost:53640";
#endif

            var placeId = serverData.placeId;
            var uni = (await services.games.MultiGetPlaceDetails(new[] { placeId })).First();
            var universeId = uni.universeId;
            var userData = await services.users.GetUserById(userId);
            var username = userData.username;
            var membership = await services.users.GetUserMembership(userId);
            var membershipType = membership?.membershipType ?? MembershipType.None;

            return "--rbxsig\r\n" + JsonConvert.SerializeObject(new
            {
                ClientPort = 0,
                MachineAddress = ip,
                ServerPort = 53640,
                PingUrl = "",
                PingInterval = 45,
                UserName = username,
                SeleniumTestMode = false,
                UserId = userId,
                SuperSafeChat = false, // always false
                CharacterAppearance = Configuration.BaseUrl + "/Asset/CharacterFetch.ashx?userId=" + userId,
                ClientTicket = DateTime.Now.ToString("MM\\/dd\\/yyyy h\\:mm tt") + ";" + ticket,
                GameId = "00000000-0000-0000-0000-000000000000", //not set rn?
                PlaceId = placeId,
                BaseUrl = "https://projex.zip",
                ChatStyle = "ClassicAndBubble", // "Classic", "Bubble", or "ClassicAndBubble"
                VendorId = 0, // don't need yet
                ScreenShotInfo = "", // don't need yet
                VideoInfo = "", // don't need yet
                CreatorId = uni.builderId,
                CreatorTypeEnum = 1,
                MembershipType = "OutrageousBuildersClub", //membershipType,
                AccountAge = accountAgeDays,
                CookieStoreEnabled = false,
                CookieStoreFirstTimePlayKey = "rbx_evt_ftp",
                CookieStoreFiveMinutePlayKey = "rbx_evt_fmp",
                IsRobloxPlace = false, // todo?
                GenerateTeleportJoin = false,
                IsUnknownOrUnder13 = false,
                SessionId = Guid.NewGuid().ToString() + "|" + new Guid().ToString() + "|" + "0" + "|" + "projex.zip" + "|" + "8" + "|" + DateTime.UtcNow.ToString("O") + "|0|null|" + ticket,
                DataCenterId = 0,
                UniverseId = universeId,
                FollowUserId = 0,
                BrowserTrackerId = 0,
                UsePortraitMode = false,
            });
        }

        [HttpGetBypass("Asset/CharacterFetch.ashx")]
        public async Task<string> CharacterFetch(long userId)
        {
            var assets = await services.avatar.GetWornAssets(userId);
            return
                $"{Configuration.BaseUrl}/Asset/BodyColors.ashx?userId={userId};{string.Join(";", assets.Select(c => Configuration.BaseUrl + "/Asset/?id=" + c))}";
        }

        private void CheckServerAuth(string auth)
        {
            if (auth != Configuration.GameServerAuthorization)
            {
                Console.WriteLine("AUTH KEY IS " + auth);
                Roblox.Metrics.GameMetrics.ReportRccAuthorizationFailure(HttpContext.Request.GetEncodedUrl(),
                    auth, GetRequesterIpRaw(HttpContext));
                throw new BadRequestException();
            }
        }

        [MVC.HttpPost("/gs/activity")]
        public async Task<dynamic> GetGsActivity([Required, MVC.FromBody] ReportActivity request)
        {
            CheckServerAuth(request.authorization);
            var result = await services.gameServer.GetLastServerPing(request.serverId);
            return new
            {
                isAlive = result >= DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1)),
                updatedAt = result,
            };
        }

        [MVC.HttpPost("/gs/ping")]
        public async Task ReportServerActivity([Required, MVC.FromBody] ReportActivity request)
        {
            CheckServerAuth(request.authorization);
            await services.gameServer.SetServerPing(request.serverId);
        }

        [MVC.HttpPost("/gs/delete")]
        public async Task DeleteServer([Required, MVC.FromBody] ReportActivity request)
        {
            CheckServerAuth(request.authorization);
            await services.gameServer.DeleteGameServer(request.serverId);
        }

        [MVC.HttpPost("/gs/shutdown")]
        public async Task ShutDownServer([Required, MVC.FromBody] ReportActivity request)
        {
            CheckServerAuth(request.authorization);
            await services.gameServer.ShutDownServer(request.serverId);
        }

        [MVC.HttpPost("/gs/players/report")]
        public async Task ReportPlayerActivity([Required, MVC.FromBody] ReportPlayerActivity request)
        {
            CheckServerAuth(request.authorization);
            if (request.eventType == "Leave")
            {
                await services.gameServer.OnPlayerLeave(request.userId, request.placeId, request.serverId);
            }
            else if (request.eventType == "Join")
            {
                //await Roblox.Metrics.GameMetrics.ReportGameJoinSuccess(request.placeId);
                await services.gameServer.OnPlayerJoin(request.userId, request.placeId, request.serverId);
            }
            else
            {
                throw new Exception("Unexpected type " + request.eventType);
            }
        }

        [MVC.HttpPost("/gs/a")]
        public void ReportGS()
        {
            // Doesn't do anything yet. See: services/api/src/controllers/bypass.ts:1473
            return;
        }

        [MVC.HttpPost("/Game/ValidateTicket.ashx")]
        public async Task<string> ValidateClientTicketRcc([Required, MVC.FromBody] ValidateTicketRequest request)
        {
#if DEBUG
            return "true";
#endif

            try
            {
                // Below is intentionally caught by local try/catch. RCC could crash if we give a 500 error.
                FeatureFlags.FeatureCheck(FeatureFlag.GamesEnabled, FeatureFlag.GameJoinEnabled);
                var ticketData = services.gameServer.DecodeTicket(request.ticket, null);
                if (ticketData.userId != request.expectedUserId)
                {
                    // Either bug or someone broke into RCC
                    Roblox.Metrics.GameMetrics.ReportTicketErrorUserIdNotMatchingTicket(request.ticket,
                        ticketData.userId, request.expectedUserId);
                    throw new Exception("Ticket userId does not match expected userId");
                }
                // From TS: it is possible for a client to spoof username or appearance to be empty string, 
                // so make sure you don't do much validation on those params (aside from assertion that it's a string)
                if (request.expectedUsername != null)
                {
                    var userInfo = await services.users.GetUserById(ticketData.userId);
                    if (userInfo.username != request.expectedUsername)
                    {
                        throw new Exception("Ticket username does not match expected username");
                    }
                }

                if (request.expectedAppearanceUrl != null)
                {
                    // will always be format of "http://localhost/Asset/CharacterFetch.ashx?userId=12", NO EXCEPTIONS!
                    var expectedUrl =
                        $"{Roblox.Configuration.BaseUrl}/Asset/CharacterFetch.ashx?userId={ticketData.userId}";
                    if (request.expectedAppearanceUrl != expectedUrl)
                    {
                        throw new Exception("Character URL is bad");
                    }
                }

                // Confirm user isn't already in a game
                var gameStatus = (await services.users.MultiGetPresence(new[] { ticketData.userId })).First();
                if (gameStatus.placeId != null && gameStatus.userPresenceType == PresenceType.InGame)
                {
                    // Make sure that the only game they are playing is the one they are trying to join.
                    var playingGames = await services.gameServer.GetGamesUserIsPlaying(ticketData.userId);
                    foreach (var game in playingGames)
                    {
                        if (game.id != request.gameJobId)
                            throw new Exception("User is already playing another game");
                    }
                }

                return "true";
            }
            catch (Exception e)
            {
                Console.WriteLine("[error] Verify ticket failed. Error = {0}\n{1}", e.Message, e.StackTrace);
                return "false";
            }
        }

        [MVC.HttpPost("/game/validate-machine")]
        public dynamic ValidateMachine()
        {
            return new
            {
                success = true,
                message = "",
            };
        }

        [HttpGetBypass("Users/ListStaff.ashx")]
        public async Task<IEnumerable<long>> GetStaffList()
        {
            return (await StaffFilter.GetStaff()).Where(c => c != 12);
        }

        [HttpGetBypass("Users/GetBanStatus.ashx")]
        public async Task<IEnumerable<dynamic>> MultiGetBanStatus(string userIds)
        {

            var ids = userIds.Split(",").Select(long.Parse).Distinct();
            var result = new List<dynamic>();
#if DEBUG
            return ids.Select(c => new
            {
                userId = c,
                isBanned = false,
            });
#else
            var multiGetResult = await services.users.MultiGetAccountStatus(ids);
            foreach (var user in multiGetResult)
            {
                result.Add(new
                {
                    userId = user.userId,
                    isBanned = user.accountStatus != AccountStatus.Ok,
                });
            }

            return result;
#endif
        }

        [HttpGetBypass("Asset/BodyColors.ashx")]
        public async Task<string> GetBodyColors(long userId)
        {
            var colors = await services.avatar.GetAvatar(userId);

            var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");

            var robloxRoot = new XElement("roblox",
                new XAttribute(XNamespace.Xmlns + "xmime", "http://www.w3.org/2005/05/xmlmime"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute(xsi + "noNamespaceSchemaLocation", "http://www.roblox.com/roblox.xsd"),
                new XAttribute("version", 4)
            );
            robloxRoot.Add(new XElement("External", "null"));
            robloxRoot.Add(new XElement("External", "nil"));
            var items = new XElement("Item", new XAttribute("class", "BodyColors"));
            var properties = new XElement("Properties");
            // set colors
            properties.Add(new XElement("int", new XAttribute("name", "HeadColor"), colors.headColorId.ToString()));
            properties.Add(new XElement("int", new XAttribute("name", "LeftArmColor"), colors.leftArmColorId.ToString()));
            properties.Add(new XElement("int", new XAttribute("name", "LeftLegColor"), colors.leftLegColorId.ToString()));
            properties.Add(new XElement("string", new XAttribute("name", "Name"), "Body Colors"));
            properties.Add(new XElement("int", new XAttribute("name", "RightArmColor"), colors.rightArmColorId.ToString()));
            properties.Add(new XElement("int", new XAttribute("name", "RightLegColor"), colors.rightLegColorId.ToString()));
            properties.Add(new XElement("int", new XAttribute("name", "TorsoColor"), colors.torsoColorId.ToString()));
            properties.Add(new XElement("bool", new XAttribute("name", "archivable"), "true"));
            // add
            items.Add(properties);
            robloxRoot.Add(items);
            // return as string
            return new XDocument(robloxRoot).ToString();
        }


        [MVC.HttpPost("api/moderation/filtertext/")]
        public dynamic GetModerationText()
        {
            var text = HttpContext.Request.Form["text"].ToString();
            return new
            {
                data = new
                {
                    white = text,
                    black = text,
                },
            };
        }

        [MVC.HttpPost("/moderation/filtertext/")]
        public dynamic GetModerationTextDupe()
        {
            var text = HttpContext.Request.Form["text"].ToString();
            return new
            {
                data = new
                {
                    white = text,
                    black = text,
                },
            };
        }
        [HttpPostBypass("/game/ChatFilter.ashx")]
        public dynamic GetChatFilter()
        {
            return "true";
        }

        private void ValidateBotAuthorization()
        {
#if DEBUG == false
	        if (Request.Headers["bot-auth"].ToString() != Roblox.Configuration.BotAuthorization)
	        {
		        throw new Exception("Internal");
	        }
#endif
        }

        [HttpGetBypass("botapi/migrate-alltypes")]
        public async Task<dynamic> MigrateAllItemsBot([Required, MVC.FromQuery] string url)
        {
            ValidateBotAuthorization();
            return await MigrateItem.MigrateItemFromRoblox(url, false, null, new List<Type>()
            {
                Type.Image,
                Type.Audio,
                Type.Mesh,
                Type.Lua,
                Type.Model,
                Type.Decal,
                Type.Animation,
                Type.SolidModel,
                Type.MeshPart,
                Type.ClimbAnimation,
                Type.DeathAnimation,
                Type.FallAnimation,
                Type.IdleAnimation,
                Type.JumpAnimation,
                Type.RunAnimation,
                Type.SwimAnimation,
                Type.WalkAnimation,
                Type.PoseAnimation,
            }, default, false);
        }

        [HttpGetBypass("botapi/migrate-clothing")]
        public async Task<dynamic> MigrateClothingBot([Required] string assetId)
        {
            ValidateBotAuthorization();
            return await MigrateItem.MigrateItemFromRoblox(assetId, true, 5, new List<Models.Assets.Type>() { Models.Assets.Type.TeeShirt, Models.Assets.Type.Shirt, Models.Assets.Type.Pants });
        }

        [HttpGetBypass("/apisite/clientsettings/Setting/QuietGet/ClientAppSettings")]
        [HttpGetBypass("/apisite/clientsettings/Setting/QuietGet/RccAppSettings")]
        [HttpGetBypass("/apisite/clientsettings/Setting/QuietGet/FireFoxAppSettings")]
        [HttpGetBypass("/apisite/clientsettings/Setting/QuietGet/ChromeAppSettings")]
        public Dictionary<string, dynamic> GetAppSettings(string? apiKey = null)
        {
            var url = HttpContext.Request.GetEncodedUrl().ToLowerInvariant();
            var isFirefox = url.Contains("firefox");
            var isChrome = !isFirefox && url.Contains("chrome");
            var isClient = url.Contains("clientappsettings");
            var isRcc = !isClient && url.Contains("rccappsettings") && apiKey == "1234";

            // Allowed values: string, int, boolean
            var flags = new FastFlagResult().AddFlags(new List<IFastFlag>
            {
                new FastFlag("FlagsLoaded", true),
                new DFastFlag("SDLRelativeMouseEnabled", true),
                new DFastFlag("SDLMousePanningFixed", true),
                new DFastFlag("OLResetPositionOnLoopStart", true, isChrome),
                new DFastFlag("OLIgnoreErrors", false),
                new FastFlag("Is18OrOverEnabled", true),
                new FastFlag("KickEnabled", true),
            });

            return flags.ToDictionary();
        }

        [HttpGetBypass("BuildersClub/Upgrade.ashx")]
        public MVC.IActionResult UpgradeNow()
        {
            return new MVC.RedirectResult("/internal/membership");
        }

        [HttpGetBypass("abusereport/UserProfile"), HttpGetBypass("abusereport/asset"), HttpGetBypass("abusereport/user"), HttpGetBypass("abusereport/users")]
        public MVC.IActionResult ReportAbuseRedirect()
        {
            return new MVC.RedirectResult("/internal/report-abuse");
        }

        [HttpGetBypass("/info/blog")]
        public MVC.IActionResult RedirectToUpdates()
        {
            return new MVC.RedirectResult("/internal/promocodes");
        }

        [HttpGetBypass("/my/economy-status")]
        public dynamic GetEconomyStatus()
        {
            return new
            {
                isMarketplaceEnabled = true,
                isMarketplaceEnabledForAuthenticatedUser = true,
                isMarketplaceEnabledForUser = true,
                isMarketplaceEnabledForGroup = true,
            };
        }

        [HttpGetBypass("/currency/balance")]
        public async Task<dynamic> GetBalance()
        {
            return await services.economy.GetBalance(CreatorType.User, safeUserSession.userId);
        }

        [HttpGetBypass("/ownership/hasasset")]
        public async Task<string> DoesOwnAsset(long userId, long assetId)
        {
            return (await services.users.GetUserAssets(userId, assetId)).Any() ? "true" : "false";
        }

        [HttpPostBypass("persistence/increment")]
        public async Task<dynamic> IncrementPersistence(long placeId, string key, string type, string scope, string target, int value)
        {
            // increment?placeId=%i&key=%s&type=%s&scope=%s&target=&value=%i

            if (!IsRcc())
                throw new RobloxException(400, 0, "BadRequest");

            return new
            {
                data = (object?)null,
            };
        }

        [HttpPostBypass("persistence/getSortedValues")]
        public async Task<dynamic> GetSortedPersistenceValues(long placeId, string type, string scope, string key, int pageSize, bool ascending, int inclusiveMinValue = 0, int inclusiveMaxValue = 0)
        {
            // persistence/getSortedValues?placeId=0&type=sorted&scope=global&key=Level%5FHighscores20&pageSize=10&ascending=False"
            // persistence/set?placeId=124921244&key=BF2%5Fds%5Ftest&&type=standard&scope=global&target=BF2%5Fds%5Fkey%5Ftmp&valueLength=31

            if (!IsRcc())
                throw new RobloxException(400, 0, "BadRequest");

            return new
            {
                data = new
                {
                    Entries = ArraySegment<int>.Empty,
                    ExclusiveStartKey = (string?)null,
                },
            };
        }



        [HttpPostBypass("persistence/set")]
        public async Task<dynamic> Set(long placeId, string key, string type, string scope, string target, int valueLength, [Required, MVC.FromBody] SetRequest request)
        {
            // { "data" : value }
            if (!IsRcc())
                throw new RobloxException(400, 0, "BadRequest");
            await ServiceProvider.GetOrCreate<DataStoreService>()
                .Set(placeId, target, type, scope, key, valueLength, request.data);

            return new
            {
                data = request.data,
            };
        }
    }
}

