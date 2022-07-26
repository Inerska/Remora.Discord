//
//  CommandTreeExtensions.cs
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
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Humanizer;
using JetBrains.Annotations;
using OneOf;
using Remora.Commands;
using Remora.Commands.Signatures;
using Remora.Commands.Trees;
using Remora.Commands.Trees.Nodes;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Services;
using Remora.Rest.Core;
using static Remora.Discord.API.Abstractions.Objects.ApplicationCommandOptionType;

namespace Remora.Discord.Commands.Extensions;

/// <summary>
/// Defines extension methods for the <see cref="CommandTree"/> class.
/// </summary>
[PublicAPI]
public static class CommandTreeExtensions
{
    /*
     * Various Discord-imposed limits.
     */

    private const int _maxRootCommandsOrGroups = 100;
    private const int _maxGroupCommands = 25;
    private const int _maxChoiceValues = 25;
    private const int _maxCommandParameters = 25;
    private const int _maxCommandStringifiedLength = 4000;
    private const int _maxCommandDescriptionLength = 100;
    private const int _maxTreeDepth = 3; // Top level is a depth of 1

    /// <summary>
    /// Maps a set of Discord application commands to their respective command nodes.
    /// </summary>
    /// <param name="commandTree">The command tree.</param>
    /// <param name="discordTree">The Discord commands.</param>
    /// <returns>The node mapping.</returns>
    public static Dictionary
    <
        (Optional<Snowflake> GuildID, Snowflake CommandID),
        OneOf<IReadOnlyDictionary<string, CommandNode>, CommandNode>
    > MapDiscordCommands
    (
        this CommandTree commandTree,
        IReadOnlyList<IApplicationCommand> discordTree
    )
    {
        var map = new Dictionary
        <
            (Optional<Snowflake> GuildID, Snowflake CommandID),
            OneOf<Dictionary<string, CommandNode>, CommandNode>
        >();

        foreach (var node in discordTree)
        {
            var isContextMenuOrRootCommand = !node.Options.IsDefined(out var options) ||
                                             options.All(o => o.Type is not (SubCommand or SubCommandGroup));
            if (isContextMenuOrRootCommand)
            {
                // Context menu command
                var commandNode = commandTree.Root.Children.OfType<CommandNode>().First
                (
                    c => c.Key.Equals(node.Name, StringComparison.OrdinalIgnoreCase)
                );

                map.Add((node.GuildID, node.ID), commandNode);
                continue;
            }

            if (!node.Options.IsDefined(out options))
            {
                // Group without children?
                throw new InvalidOperationException();
            }

            foreach (var nodeOption in options)
            {
                var subcommands = MapDiscordOptions(commandTree, new List<string> { node.Name }, nodeOption);
                if (!map.TryGetValue((node.GuildID, node.ID), out var value))
                {
                    var subMap = new Dictionary<string, CommandNode>();
                    map.Add((node.GuildID, node.ID), subMap);
                    value = subMap;
                }

                var groupMap = value.AsT0;
                foreach (var (path, subNode) in subcommands)
                {
                    groupMap.Add(string.Join("::", path), subNode);
                }
            }
        }

        return map.ToDictionary
        (
            kvp => kvp.Key,
            kvp =>
            {
                var (_, value) = kvp;
                return value.IsT0
                    ? OneOf<IReadOnlyDictionary<string, CommandNode>, CommandNode>.FromT0(value.AsT0)
                    : OneOf<IReadOnlyDictionary<string, CommandNode>, CommandNode>.FromT1(value.AsT1);
            }
        );
    }

    /// <summary>
    /// Converts the command tree to a set of Discord application commands.
    /// </summary>
    /// <param name="tree">The command tree.</param>
    /// <returns>A set of bulk application command data.</returns>
    public static IReadOnlyList<IBulkApplicationCommandData> CreateApplicationCommands
    (
        this CommandTree tree
    )
        => CreateApplicationCommands(tree, null);

    /// <summary>
    /// Converts the command tree to a set of Discord application commands.
    /// </summary>
    /// <param name="tree">The command tree.</param>
    /// <param name="localizationProvider">The localization provider.</param>
    /// <returns>A set of bulk application command data.</returns>
    public static IReadOnlyList<IBulkApplicationCommandData> CreateApplicationCommands
    (
        this CommandTree tree,
        ILocalizationProvider? localizationProvider
    )
    {
        localizationProvider ??= new NullLocalizationProvider();

        var commands = new List<BulkApplicationCommandData>();
        var commandNames = new Dictionary<int, HashSet<string>>();
        foreach (var node in tree.Root.Children)
        {
            // Using the TryTranslateCommandNode() method here for the sake of code simplicity, even though it
            // returns an "option" object, which isn't truly what we want.
            var option = TranslateCommandNode(node, 1, localizationProvider);

            if (option is null)
            {
                // skipped
                continue;
            }

            // Perform validations

            // int is used as a lookup here because dictionaries can't have null keys
            var type = node is CommandNode command ? (int)command.GetCommandType() : -1;
            if (!commandNames.TryGetValue(type, out var names))
            {
                names = new HashSet<string>();
                commandNames.Add(type, names);
            }

            if (!names.Add(option.Name))
            {
                throw new UnsupportedFeatureException("Overloads are not supported.", node);
            }

            if (GetCommandStringifiedLength(option) > _maxCommandStringifiedLength)
            {
                throw new UnsupportedFeatureException
                (
                    "One or more commands is too long (combined length of name, description, and value " +
                    $"properties), max {_maxCommandStringifiedLength}).",
                    node
                );
            }

            // Translate from options to bulk data
            var (commandType, directMessagePermission, defaultMemberPermissions) = GetNodeMetadata(node);

            var localizedNames = localizationProvider.GetStrings(option.Name);
            var localizedDescriptions = localizationProvider.GetStrings(option.Description);

            commands.Add
            (
                new BulkApplicationCommandData
                (
                    option.Name,
                    option.Description,
                    default,
                    option.Options,
                    commandType,
                    localizedNames.Count > 0 ? new(localizedNames) : default,
                    localizedDescriptions.Count > 0 ? new(localizedDescriptions) : default,
                    defaultMemberPermissions,
                    directMessagePermission
                )
            );
        }

        // Perform validations
        if (commands.Count > _maxRootCommandsOrGroups)
        {
            throw new UnsupportedFeatureException
            (
                $"Too many root-level commands or groups (had {commands.Count}, max {_maxRootCommandsOrGroups}).",
                tree.Root
            );
        }

        return commands;
    }

    private static
    (
        Optional<ApplicationCommandType> CommandType,
        Optional<bool> DirectMessagePermission,
        IDiscordPermissionSet? DefaultMemberPermission
    )
    GetNodeMetadata(IChildNode node)
    {
        Optional<ApplicationCommandType> commandType = default;
        Optional<bool> directMessagePermission = default;
        IDiscordPermissionSet? defaultMemberPermissions = default;

        switch (node)
        {
            case GroupNode groupNode:
            {
                var memberPermissionAttributes = groupNode.GroupTypes.Select
                (
                    t => t.GetCustomAttribute<DiscordDefaultMemberPermissionsAttribute>()
                )
                .Where(attribute => attribute is not null)
                .ToArray();

                var directMessagePermissionAttributes = groupNode.GroupTypes.Select
                (
                    t => t.GetCustomAttribute<DiscordDefaultDMPermissionAttribute>()
                )
                .Where(attribute => attribute is not null)
                .ToArray();

                if (memberPermissionAttributes.Length > 1)
                {
                    throw new UnsupportedFeatureException
                    (
                        "In a set of groups with the same name, only one may be marked with a default " +
                        $"member permissions attribute, but {memberPermissionAttributes.Length} were found.",
                        node
                    );
                }

                var defaultMemberPermissionsAttribute = memberPermissionAttributes.SingleOrDefault();

                if (defaultMemberPermissionsAttribute is not null)
                {
                    defaultMemberPermissions = new DiscordPermissionSet
                    (
                        defaultMemberPermissionsAttribute.Permissions.ToArray()
                    );
                }

                if (directMessagePermissionAttributes.Length > 1)
                {
                    throw new UnsupportedFeatureException
                    (
                        "In a set of groups with the same name, only one may be marked with a default " +
                        $"DM permissions attribute, but {memberPermissionAttributes.Length} were found.",
                        node
                    );
                }

                var directMessagePermissionAttribute = directMessagePermissionAttributes.SingleOrDefault();

                if (directMessagePermissionAttribute is not null)
                {
                    directMessagePermission = directMessagePermissionAttribute.IsExecutableInDMs;
                }

                break;
            }
            case CommandNode commandNode:
            {
                commandType = commandNode.GetCommandType();

                // Top-level command outside of a group
                var memberPermissionsAttribute =
                    commandNode.GroupType.GetCustomAttribute<DiscordDefaultMemberPermissionsAttribute>() ??
                    commandNode.CommandMethod.GetCustomAttribute<DiscordDefaultMemberPermissionsAttribute>();

                var directMessagePermissionAttribute =
                    commandNode.GroupType.GetCustomAttribute<DiscordDefaultDMPermissionAttribute>() ??
                    commandNode.CommandMethod.GetCustomAttribute<DiscordDefaultDMPermissionAttribute>();

                if (memberPermissionsAttribute is not null)
                {
                    defaultMemberPermissions = new DiscordPermissionSet
                    (
                        memberPermissionsAttribute.Permissions.ToArray()
                    );
                }

                if (directMessagePermissionAttribute is not null)
                {
                    directMessagePermission = directMessagePermissionAttribute.IsExecutableInDMs;
                }

                break;
            }
        }

        return (commandType, directMessagePermission, defaultMemberPermissions);
    }

    private static IApplicationCommandOption? TranslateCommandNode
    (
        IChildNode node,
        int treeDepth,
        ILocalizationProvider localizationProvider
    )
    {
        if (treeDepth > _maxTreeDepth)
        {
            throw new UnsupportedFeatureException
            (
                $"A sub-command or group was nested too deeply (depth {treeDepth}, max {_maxTreeDepth}).",
                node
            );
        }

        return node switch
        {
            CommandNode command => TranslateCommandNode(command, treeDepth, localizationProvider),
            GroupNode group => TranslateGroupNode(group, treeDepth, localizationProvider),
            _ => throw new InvalidOperationException
            (
                $"Unable to translate node of type {node.GetType().FullName} into an application command."
            )
        };
    }

    private static IApplicationCommandOption? TranslateGroupNode
    (
        GroupNode group,
        int treeDepth,
        ILocalizationProvider localizationProvider
    )
    {
        ValidateNodeDescription(group.Description, group);

        var groupOptions = new List<IApplicationCommandOption>();
        var groupOptionNames = new HashSet<string>();
        var subCommandCount = 0;
        foreach (var childNode in group.Children)
        {
            var translatedCommandNode = TranslateCommandNode
            (
                childNode,
                treeDepth + 1,
                localizationProvider
            );

            if (translatedCommandNode is null)
            {
                // Skipped
                continue;
            }

            if (translatedCommandNode.Type is SubCommand)
            {
                ++subCommandCount;
            }

            if (!groupOptionNames.Add(translatedCommandNode.Name))
            {
                throw new UnsupportedFeatureException("Overloads are not supported.", group);
            }

            groupOptions.Add(translatedCommandNode);
        }

        if (groupOptions.Count == 0)
        {
            return null;
        }

        if (subCommandCount > _maxGroupCommands)
        {
            throw new UnsupportedFeatureException
            (
                $"Too many commands under a group ({subCommandCount}, max {_maxGroupCommands}).",
                group
            );
        }

        var name = group.Key.ToLowerInvariant();

        var localizedNames = localizationProvider.GetStrings(name);
        var localizedDescriptions = localizationProvider.GetStrings(group.Description);

        return new ApplicationCommandOption
        (
            SubCommandGroup,
            name,
            group.Description,
            Options: new(groupOptions),
            NameLocalizations: localizedNames.Count > 0 ? new(localizedNames) : default,
            DescriptionLocalizations: localizedDescriptions.Count > 0 ? new(localizedDescriptions) : default
        );
    }

    private static IApplicationCommandOption? TranslateCommandNode
    (
        CommandNode command,
        int treeDepth,
        ILocalizationProvider localizationProvider
    )
    {
        if (command.FindCustomAttributeOnLocalTree<ExcludeFromSlashCommandsAttribute>() is not null)
        {
            return null;
        }

        var commandType = command.GetCommandType();
        if (commandType is not ApplicationCommandType.ChatInput)
        {
            if (treeDepth > 1)
            {
                throw new UnsupportedFeatureException
                (
                    "Context menu commands may not be nested.",
                    command
                );
            }

            var parameters = command.CommandMethod.GetParameters();
            if (parameters.Length > 0)
            {
                var expectedParameter = commandType.AsParameterName();
                if (parameters.Length != 1 || parameters[0].Name != expectedParameter)
                {
                    throw new UnsupportedFeatureException
                    (
                        $"{commandType.Humanize()} context menu commands may only have a single parameter named {expectedParameter}.",
                        command
                    );
                }
            }
        }

        var options = default(Optional<IReadOnlyList<IApplicationCommandOption>>);

        // Options are only supported by slash commands (ChatInput)
        if (command.GetCommandType() is ApplicationCommandType.ChatInput)
        {
            options = new(CreateCommandParameterOptions(command, localizationProvider));
        }

        var name = commandType is not ApplicationCommandType.ChatInput
            ? command.Key
            : command.Key.ToLowerInvariant();

        var description = commandType is not ApplicationCommandType.ChatInput
            ? string.Empty
            : command.Shape.Description;

        if (commandType is not ApplicationCommandType.ChatInput &&
            command.Shape.Description != Constants.DefaultDescription)
        {
            throw new UnsupportedFeatureException("Descriptions are not allowed on context menu commands.", command);
        }

        ValidateNodeDescription(description, command);

        var localizedNames = localizationProvider.GetStrings(name);
        var localizedDescriptions = localizationProvider.GetStrings(description);

        // Might not actually be a sub-command, but the caller will handle that
        // TODO: Should this just use commandType directly?
        return new ApplicationCommandOption
        (
            SubCommand,
            name,
            description,
            Options: options,
            NameLocalizations: localizedNames.Count > 0 ? new(localizedNames) : default,
            DescriptionLocalizations: localizedDescriptions.Count > 0 ? new(localizedDescriptions) : default
        );
    }

    private static IReadOnlyList<IApplicationCommandOption> CreateCommandParameterOptions
    (
        CommandNode command,
        ILocalizationProvider localizationProvider
    )
    {
        var parameterOptions = new List<IApplicationCommandOption>();
        foreach (var parameter in command.Shape.Parameters)
        {
            ValidateNodeDescription(parameter.Description, command);

            switch (parameter)
            {
                case SwitchParameterShape:
                {
                    throw new UnsupportedParameterFeatureException
                    (
                        "Switch parameters are not supported.",
                        command,
                        parameter
                    );
                }
                case NamedCollectionParameterShape or PositionalCollectionParameterShape:
                {
                    throw new UnsupportedParameterFeatureException
                    (
                        "Collection parameters are not supported.",
                        command,
                        parameter
                    );
                }
            }

            var actualParameterType = parameter.GetActualParameterType();
            var discordType = parameter.GetDiscordType();

            var channelTypes = CreateChannelTypesOption(command, parameter, discordType);

            var (enableAutocomplete, choices) = GetParameterChoices
            (
                parameter.Parameter,
                actualParameterType,
                localizationProvider
            );

            var minValue = parameter.Parameter.GetCustomAttribute<MinValueAttribute>();
            var maxValue = parameter.Parameter.GetCustomAttribute<MaxValueAttribute>();

            if (discordType is not Integer or Number && (minValue is not null || maxValue is not null))
            {
                throw new UnsupportedParameterFeatureException
                (
                    "A non-numerical parameter may not specify a minimum or maximum value.",
                    command,
                    parameter
                );
            }

            var minLength = parameter.Parameter.GetCustomAttribute<MinLengthAttribute>();
            var maxLength = parameter.Parameter.GetCustomAttribute<MaxLengthAttribute>();

            if (discordType is not ApplicationCommandOptionType.String && (minLength is not null || maxLength is not null))
            {
                throw new UnsupportedParameterFeatureException
                (
                    "A non-string parameter may not specify a minimum or maximum length.",
                    command,
                    parameter
                );
            }

            if (minLength?.Length is < 0)
            {
                throw new UnsupportedParameterFeatureException
                (
                    "The minimum length must be more than 0.",
                    command,
                    parameter
                );
            }

            if (maxLength?.Length is < 1)
            {
                throw new UnsupportedParameterFeatureException
                (
                    "The maximum length must be more than 1.",
                    command,
                    parameter
                );
            }

            var name = parameter.HintName.ToLowerInvariant();
            var description = parameter.Description;

            var localizedNames = localizationProvider.GetStrings(name);
            var localizedDescriptions = localizationProvider.GetStrings(description);

            var parameterOption = new ApplicationCommandOption
            (
                discordType,
                name,
                parameter.Description,
                default,
                !parameter.IsOmissible(),
                choices,
                ChannelTypes: channelTypes,
                EnableAutocomplete: enableAutocomplete,
                MinValue: minValue?.Value ?? default(Optional<OneOf<ulong, long, float, double>>),
                MaxValue: maxValue?.Value ?? default(Optional<OneOf<ulong, long, float, double>>),
                NameLocalizations: localizedNames.Count > 0 ? new(localizedNames) : default,
                DescriptionLocalizations: localizedDescriptions.Count > 0 ? new(localizedDescriptions) : default,
                MinLength: (uint?)minLength?.Length ?? default(Optional<uint>),
                MaxLength: (uint?)maxLength?.Length ?? default(Optional<uint>)
            );

            parameterOptions.Add(parameterOption);
        }

        if (parameterOptions.Count > _maxCommandParameters)
        {
            throw new UnsupportedFeatureException
            (
                $"Too many parameters in a command (had {parameterOptions.Count}, max {_maxCommandParameters}).",
                command
            );
        }

        return parameterOptions;
    }

    private static (Optional<bool> EnableAutocomplete, Optional<IReadOnlyList<IApplicationCommandOptionChoice>> Choices)
    GetParameterChoices
    (
        ParameterInfo parameter,
        Type actualParameterType,
        ILocalizationProvider localizationProvider
    )
    {
        Optional<bool> enableAutocomplete = default;
        Optional<IReadOnlyList<IApplicationCommandOptionChoice>> choices = default;

        if (actualParameterType.IsEnum)
        {
            // Add the choices directly
            if (Enum.GetValues(actualParameterType).Length <= _maxChoiceValues)
            {
                choices = new(EnumExtensions.GetEnumChoices(actualParameterType, localizationProvider));
            }
            else
            {
                // Enable autocomplete for this enum type
                enableAutocomplete = true;
            }
        }
        else
        {
            if (parameter.GetCustomAttribute<AutocompleteAttribute>() is not null)
            {
                enableAutocomplete = true;
            }
        }

        return (enableAutocomplete, choices);
    }

    private static Optional<IReadOnlyList<ChannelType>> CreateChannelTypesOption
    (
        CommandNode command,
        IParameterShape parameter,
        ApplicationCommandOptionType parameterType
    )
    {
        var channelTypesAttribute = parameter.Parameter.GetCustomAttribute<ChannelTypesAttribute>();
        if (channelTypesAttribute is not null && parameterType is not ApplicationCommandOptionType.Channel)
        {
            throw new UnsupportedParameterFeatureException
            (
                $"The {nameof(ChannelTypesAttribute)} can only be used on a parameter of type {nameof(IChannel)}.",
                command,
                parameter
            );
        }

        var channelTypes = channelTypesAttribute is null
            ? default
            : new Optional<IReadOnlyList<ChannelType>>(channelTypesAttribute.Types);

        if (channelTypes.HasValue && channelTypes.Value.Count == 0)
        {
            throw new UnsupportedParameterFeatureException
            (
                $"Using {nameof(ChannelTypesAttribute)} requires at least one {nameof(ChannelType)} to be provided.",
                command,
                parameter
            );
        }

        return channelTypes;
    }

    private static int GetCommandStringifiedLength(IApplicationCommandOption option)
    {
        var length = 0;
        length += option.Name.Length;
        length += option.Description.Length;

        if (option.Choices.IsDefined(out var choices))
        {
            foreach (var choice in choices)
            {
                length += choice.Name.Length;
                if (choice.Value.TryPickT0(out var choiceValue, out _))
                {
                    length += choiceValue.Length;
                }
            }
        }

        if (option.Options.IsDefined(out var options))
        {
            length += options.Sum(GetCommandStringifiedLength);
        }

        return length;
    }

    private static void ValidateNodeDescription(string description, IChildNode node)
    {
        switch (node)
        {
            case CommandNode command:
            {
                var type = command.GetCommandType();
                if (type is ApplicationCommandType.ChatInput)
                {
                    if (description.Length <= _maxCommandDescriptionLength)
                    {
                        return;
                    }

                    throw new UnsupportedFeatureException
                    (
                        $"A command description was too long (length {description.Length}, "
                        + $"max {_maxCommandDescriptionLength}).",
                        node
                    );
                }

                if (description == string.Empty)
                {
                    return;
                }

                throw new UnsupportedFeatureException
                (
                   "Descriptions are not allowed on context menu commands.",
                   node
                );
            }
            default:
            {
                // Assume it uses the default limits
                if (description.Length <= _maxCommandDescriptionLength)
                {
                    return;
                }

                throw new UnsupportedFeatureException
                (
                    $"A group or parameter description was too long (length {description.Length}, "
                    + $"max {_maxCommandDescriptionLength}).",
                    node
                );
            }
        }
    }

    private static IEnumerable<(List<string> Path, CommandNode Node)> MapDiscordOptions
    (
        CommandTree commandTree,
        List<string> outerPath,
        IApplicationCommandOption commandOption
    )
    {
        if (commandOption.Type is SubCommand)
        {
            // Find the node
            var pathComponent = outerPath.First();
            var depth = 0;

            IParentNode currentNode = commandTree.Root;
            while (true)
            {
                var nodeByPath = currentNode.Children.First
                (
                    c => c.Key.Equals(pathComponent ?? commandOption.Name, StringComparison.OrdinalIgnoreCase)
                );

                if (nodeByPath is IParentNode groupNode)
                {
                    ++depth;

                    pathComponent = outerPath.Skip(depth).FirstOrDefault();
                    currentNode = groupNode;
                    continue;
                }

                if (nodeByPath is not CommandNode commandNode)
                {
                    throw new InvalidOperationException("Unknown node type.");
                }

                yield return (outerPath.Append(commandNode.Key).ToList(), commandNode);
                yield break;
            }
        }

        if (commandOption.Type is not SubCommandGroup)
        {
            throw new InvalidOperationException("Unknown option type.");
        }

        var subcommands = commandOption.Options.Value
            .Select(o => MapDiscordOptions(commandTree, outerPath.Append(commandOption.Name).ToList(), o));

        foreach (var subcommand in subcommands.SelectMany(x => x))
        {
            yield return subcommand;
        }
    }
}
