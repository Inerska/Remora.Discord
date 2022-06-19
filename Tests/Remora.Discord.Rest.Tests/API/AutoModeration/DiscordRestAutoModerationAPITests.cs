//
//  DiscordRestAutoModerationAPITests.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2017 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Remora.Discord.API;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Rest.API;
using Remora.Discord.Rest.Tests.TestBases;
using Remora.Discord.Tests;
using Remora.Rest.Core;
using Remora.Rest.Xunit.Extensions;
using RichardSzalay.MockHttp;
using Xunit;

namespace Remora.Discord.Rest.Tests.API.AutoModeration;

/// <summary>
/// Tests the <see cref="DiscordRestAutoModerationAPI"/> class.
/// </summary>
public class DiscordRestAutoModerationAPITests
{
    /// <summary>
    /// Tests the <see cref="DiscordRestAutoModerationAPI.ListGuildAutoModerationRulesAsync"/> method.
    /// </summary>
    public class ListGuildAutoModerationRulesAsync : RestAPITestBase<IDiscordRestAutoModerationAPI>
    {
        /// <summary>
        /// Tests whether the API method performs its request correctly.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [Fact]
        public async Task PerformsRequestCorrectly()
        {
            var guildID = DiscordSnowflake.New(0);

            var api = CreateAPI
            (
                b => b
                    .Expect(HttpMethod.Get, $"{Constants.BaseURL}guilds/{guildID}/auto-moderation/rules")
                    .Respond("application/json", "[ ]")
            );

            var result = await api.ListGuildAutoModerationRulesAsync(guildID);
            ResultAssert.Successful(result);
        }
    }

    /// <summary>
    /// Tests the <see cref="DiscordRestAutoModerationAPI.GetGuildAutoModerationRuleAsync"/> method.
    /// </summary>
    public class GetGuildAutoModerationRuleAsync : RestAPITestBase<IDiscordRestAutoModerationAPI>
    {
        /// <summary>
        /// Tests whether the API method performs its request correctly.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [Fact]
        public async Task PerformsRequestCorrectly()
        {
            var guildID = DiscordSnowflake.New(0);
            var ruleID = DiscordSnowflake.New(0);

            var api = CreateAPI
            (
                b => b
                    .Expect(HttpMethod.Get, $"{Constants.BaseURL}guilds/{guildID}/auto-moderation/rules/{ruleID}")
                    .Respond("application/json", SampleRepository.Samples[typeof(IAutoModerationRule)])
            );

            var result = await api.GetGuildAutoModerationRuleAsync(guildID, ruleID);
            ResultAssert.Successful(result);
        }
    }

    /// <summary>
    /// Tests the <see cref="DiscordRestAutoModerationAPI.CreateGuildAutoModerationRuleAsync"/> method.
    /// </summary>
    public class CreateGuildAutoModerationRuleAsync : RestAPITestBase<IDiscordRestAutoModerationAPI>
    {
        /// <summary>
        /// Tests whether the API method performs its request correctly.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [Fact]
        public async Task PerformsRequestCorrectly()
        {
            var guildID = DiscordSnowflake.New(0);
            var name = "test";
            var eventType = AutoModerationEventType.MessageSend;
            var triggerType = AutoModerationTriggerType.Keyword;
            var triggerMetadata = new AutoModerationTriggerMetadata
            (
                KeywordFilter: new List<string>(),
                Presets: new List<AutoModerationKeywordPresetType>()
            );
            var actions = new List<IAutoModerationAction>();
            var enabled = false;
            var exemptRoles = new List<Snowflake>();
            var exemptChannels = new List<Snowflake>();

            var api = CreateAPI
            (
                b => b
                    .Expect(HttpMethod.Post, $"{Constants.BaseURL}guilds/{guildID}/auto-moderation/rules")
                    .WithJson
                    (
                        j => j.IsObject
                        (
                            o => o
                                .WithProperty("name", p => p.Is(name))
                                .WithProperty("event_type", p => p.Is((int)eventType))
                                .WithProperty("trigger_type", p => p.Is((int)triggerType))
                                .WithProperty("trigger_metadata", p => p.IsObject
                                (
                                    o => o
                                        .WithProperty("keyword_filter", p => p.IsArray(a => a.WithCount(0)))
                                        .WithProperty("presets", p => p.IsArray(a => a.WithCount(0)))
                                ))
                                .WithProperty("actions", p => p.IsArray(a => a.WithCount(0)))
                                .WithProperty("enabled", p => p.Is(enabled))
                                .WithProperty("exempt_roles", p => p.IsArray(a => a.WithCount(0)))
                                .WithProperty("exempt_channels", p => p.IsArray(a => a.WithCount(0)))
                        )
                    )
                    .Respond("application/json", SampleRepository.Samples[typeof(IAutoModerationRule)])
            );

            var result = await api.CreateGuildAutoModerationRuleAsync
            (
                guildID,
                name,
                eventType,
                triggerType,
                triggerMetadata,
                actions,
                enabled,
                exemptRoles,
                exemptChannels
            );

            ResultAssert.Successful(result);
        }
    }

    /// <summary>
    /// Tests the <see cref="DiscordRestAutoModerationAPI.ModifyGuildAutoModerationRuleAsync"/> method.
    /// </summary>
    public class ModifyGuildAutoModerationRuleAsync : RestAPITestBase<IDiscordRestAutoModerationAPI>
    {
        /// <summary>
        /// Tests whether the API method performs its request correctly.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [Fact]
        public async Task PerformsRequestCorrectly()
        {
            var guildID = DiscordSnowflake.New(0);
            var ruleID = DiscordSnowflake.New(1);
            var name = "test";
            var eventType = AutoModerationEventType.MessageSend;
            var triggerType = AutoModerationTriggerType.Keyword;
            var triggerMetadata = new AutoModerationTriggerMetadata
            (
                KeywordFilter: new List<string>(),
                Presets: new List<AutoModerationKeywordPresetType>()
            );
            var actions = new List<IAutoModerationAction>();
            var enabled = false;
            var exemptRoles = new List<Snowflake>();
            var exemptChannels = new List<Snowflake>();

            var api = CreateAPI
            (
                b => b
                    .Expect(HttpMethod.Patch, $"{Constants.BaseURL}guilds/{guildID}/auto-moderation/rules/{ruleID}")
                    .WithJson
                    (
                        j => j.IsObject
                        (
                            o => o
                                .WithProperty("name", p => p.Is(name))
                                .WithProperty("event_type", p => p.Is((int)eventType))
                                .WithProperty("trigger_type", p => p.Is((int)triggerType))
                                .WithProperty("trigger_metadata", p => p.IsObject
                                (
                                    o => o
                                        .WithProperty("keyword_filter", p => p.IsArray(a => a.WithCount(0)))
                                        .WithProperty("presets", p => p.IsArray(a => a.WithCount(0)))
                                ))
                                .WithProperty("actions", p => p.IsArray(a => a.WithCount(0)))
                                .WithProperty("enabled", p => p.Is(enabled))
                                .WithProperty("exempt_roles", p => p.IsArray(a => a.WithCount(0)))
                                .WithProperty("exempt_channels", p => p.IsArray(a => a.WithCount(0)))
                        )
                    )
                    .Respond("application/json", SampleRepository.Samples[typeof(IAutoModerationRule)])
            );

            var result = await api.ModifyGuildAutoModerationRuleAsync
            (
                guildID,
                ruleID,
                name,
                eventType,
                triggerType,
                triggerMetadata,
                actions,
                enabled,
                exemptRoles,
                exemptChannels
            );

            ResultAssert.Successful(result);
        }
    }

    /// <summary>
    /// Tests the <see cref="DiscordRestAutoModerationAPI.DeleteGuildAutoModerationRuleAsync"/> method.
    /// </summary>
    public class DeleteGuildAutoModerationRuleAsync : RestAPITestBase<IDiscordRestAutoModerationAPI>
    {
        /// <summary>
        /// Tests whether the API method performs its request correctly.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [Fact]
        public async Task PerformsRequestCorrectly()
        {
            var guildID = DiscordSnowflake.New(0);
            var ruleID = DiscordSnowflake.New(1);

            var api = CreateAPI
            (
                b => b
                    .Expect(HttpMethod.Delete, $"{Constants.BaseURL}guilds/{guildID}/auto-moderation/rules/{ruleID}")
                    .Respond("application/json", SampleRepository.Samples[typeof(IAutoModerationRule)])
            );

            var result = await api.DeleteGuildAutoModerationRuleAsync
            (
                guildID,
                ruleID
            );

            ResultAssert.Successful(result);
        }
    }
}
