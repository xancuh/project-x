using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Roblox.Logging;
using Roblox.Website.Controllers;
using Roblox.Website.WebsiteModels.Asset;
using Roblox.Models.AbuseReport;
using Roblox.Models.Assets;
using Roblox.Models.Avatar;
using Roblox.Models.Db;
using Roblox.Models.Economy;
using Roblox.Models.Sessions;
using Roblox.Models.Staff;
using Roblox.Models.Trades;
using Roblox.Models.Users;
using Roblox.Services.App.FeatureFlags;
using Roblox.Services.Exceptions;
using Roblox.Website.Filters;
using Npgsql;
// using Roblox.Website.WebsiteModels.Asset;
using Type = Roblox.Models.Assets.Type;
// using Roblox.Services.AssetsService;
// using Roblox.Models.Assets
// using Roblox.Models.Db;
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using System.Collections.Generic;

namespace Roblox.Website.Pages.Internal;

public class Promocodes : RobloxPageModel
{
    private NpgsqlConnection db => services.assets.db;
    [BindProperty]
    public string? promocode { get; set; }
    public string? failureMessage { get; set; }
    public string? successMessage { get; set; }
    public void OnGet()
    {
    }

public async Task OnPost()
{
    if (string.IsNullOrWhiteSpace(promocode))
    {
        failureMessage = "Empty promocode. Please paste a promocode.";
        return;
    }

    try
    {
        // using var ec = Roblox.Services.ServiceProvider.GetOrCreate<EconomyService>;
        var promoCodes = promocode.Split(',');

        var itemsAdded = 0; // Counter to track the number of items successfully added
        var robuxAdded = 0;
        var tixAdded = 0;
        var promocodesUsed = 0;
        // using var ec = ServiceProvider.GetOrCreate<EconomyService>();

        foreach (var code in promoCodes)
        {
            // Check if the promocode exists and has uses left
            var promocodeData = await db.QuerySingleOrDefaultAsync<PromocodeData>(
                "SELECT asset_ids, uses, robux, tix FROM promocodes WHERE promocode ILIKE @promocode",
                new { promocode = code.Trim() }
            );

            if (promocodeData != null && promocodeData.uses > 0)
            {
                promocodesUsed = promocodesUsed + 1;
                var assetIds = promocodeData.asset_ids.Split(',');
                // var redeemed = promocodeData.redeemed
                var userHasAllItems = true; // Flag to track if the user already has all the items
                foreach (var assetId in assetIds)
                {
                    if (promocodeData.asset_ids == "null" || promocodeData.asset_ids == "none") {
                        userHasAllItems = false;
                        robuxAdded = 0;
                        break;
                    }
                    var existingAssetId = await db.QuerySingleOrDefaultAsync<int?>(
                        "SELECT asset_id FROM user_asset WHERE user_id = @user_id AND asset_id = @asset_id",
                        new { user_id = userSession.userId, asset_id = int.Parse(assetId.Trim()) }
                    );

                    if (!existingAssetId.HasValue)
                    {
                        userHasAllItems = false; // User does not have at least one item, so it's not considered having all items
                        // Perform the insert query
                        var id = await db.QuerySingleOrDefaultAsync<int>(
                            "INSERT INTO user_asset (asset_id, user_id, serial) VALUES (@asset_id, @user_id, @serial) RETURNING user_asset.id",
                            new { asset_id = int.Parse(assetId.Trim()), user_id = userSession.userId, serial = (long?)null }
                        );

                        if (id <= 0)
                        {
                            failureMessage = "Failed to add the item to the user's inventory.";
                            return;
                        }

                        itemsAdded++;
                    }
                }

                if (userHasAllItems)
                {
                    failureMessage = "You already have all the items from the redeemed promocode(s).";
                    return;
                }

                // Decrement the uses_left for the promocode
                await db.ExecuteAsync(
                    "UPDATE promocodes SET uses = uses - 1 WHERE promocode ILIKE @promocode",
                    new { promocode = code.Trim() }
                );
                // var robux = promocodeData.robux;
                // Check if the promocode grants Tix or Robux
            // var robux = await db.QuerySingleOrDefaultAsync<PromocodeData>(
                // "SELECT balance_robux, balance_tix FROM promocodes WHERE promocode ILIKE @promocode",
                // new { promocode = code.Trim() }
            // );
        if (promocodeData.tix != 0 || promocodeData.robux != 0)
        {
            await db.ExecuteAsync(
            "UPDATE user_economy SET balance_tickets = balance_tickets + @tix, balance_robux = balance_robux + @robux WHERE user_id = @user_id",
            new { tix = promocodeData.tix, robux = promocodeData.robux, user_id = userSession.userId }
                );
            robuxAdded = robuxAdded + promocodeData.robux;
            tixAdded = tixAdded + promocodeData.tix;
        }
            }
            else
            {
                failureMessage = "This promocode does not exist.";
                return;
            }
        }

        if (itemsAdded > 0 || robuxAdded > 0 || tixAdded >  0)
        {
            successMessage = $"{promocodesUsed} promocode(s) successfully used.";
        }
        else
        {
            failureMessage = "No new robux/tix/items were added to your inventory.";
        }
    }
    catch (Exception e)
    {
        Writer.Info(LogGroup.HttpRequest, "Failed to check or use the promocode. Error message = {0} Stack trace: {1}", e.Message, e.StackTrace);
        failureMessage = "Something went wrong while checking or using the promocode.";
    }
}
        private class PromocodeData
        {
            public string asset_ids { get; set; }
            public int uses { get; set; }
            public int robux { get; set; }
            public int tix { get; set; }
            // public string redeemed { get; set; }
        }
    }