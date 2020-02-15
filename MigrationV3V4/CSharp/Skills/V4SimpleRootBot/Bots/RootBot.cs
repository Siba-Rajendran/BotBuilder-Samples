﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core.Skills;
using Microsoft.Bot.Builder.Skills;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Microsoft.BotBuilderSamples.SimpleRootBot.Bots
{
    public class RootBot : ActivityHandler
    {
        private readonly IStatePropertyAccessor<BotFrameworkSkill> _activeSkillProperty;
        private readonly string _botId;
        private readonly ConversationState _conversationState;
        private readonly SkillHttpClient _skillClient;
        private readonly SkillsConfiguration _skillsConfig;

        public RootBot(ConversationState conversationState, SkillsConfiguration skillsConfig, SkillHttpClient skillClient, IConfiguration configuration)
        {
            _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));
            _skillsConfig = skillsConfig ?? throw new ArgumentNullException(nameof(skillsConfig));
            _skillClient = skillClient ?? throw new ArgumentNullException(nameof(skillsConfig));
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            _botId = configuration.GetSection(MicrosoftAppCredentials.MicrosoftAppIdKey)?.Value;
            if (string.IsNullOrWhiteSpace(_botId))
            {
                throw new ArgumentException($"{MicrosoftAppCredentials.MicrosoftAppIdKey} is not set in configuration");
            }

            // Create state property to track the active skill
            _activeSkillProperty = conversationState.CreateProperty<BotFrameworkSkill>("activeSkillProperty");
        }


        private Attachment GetOptionsAttachment()
        {
            HeroCard heroCard = null;

            if (_skillsConfig.Skills.Count > 0)
            {
                var buttons = new List<CardAction>();
                foreach (var skill in _skillsConfig.Skills)
                {
                    buttons.Add(new CardAction(ActionTypes.ImBack, title: $"{skill.Value.Id}", text: skill.Value.Id, value: skill.Value.Id));
                }
                heroCard = new HeroCard
                {
                    Title = "Skills Options",
                    Text = "Click one of the buttons below to initiate that skill.",
                    Buttons = buttons
                };
            }
            else
            {
                heroCard = new HeroCard
                {
                    Title = "No Skills configured...",
                    Subtitle = "Configure some skills in appsettings.json",
                };
            }

            return heroCard.ToAttachment();
        }

        private async Task SendToSkill(ITurnContext<IMessageActivity> turnContext, BotFrameworkSkill targetSkill, CancellationToken cancellationToken)
        {
            // NOTE: Always SaveChanges() before calling a skill so that any activity generated by the skill
            // will have access to current accurate state.
            await _conversationState.SaveChangesAsync(turnContext, force: true, cancellationToken: cancellationToken);

            // route the activity to the skill
            var response = await _skillClient.PostActivityAsync(_botId, targetSkill, _skillsConfig.SkillHostEndpoint, (Activity)turnContext.Activity, cancellationToken);

            // Check response status
            if (!(response.Status >= 200 && response.Status <= 299))
            {
                throw new HttpRequestException($"Error invoking the skill id: \"{targetSkill.Id}\" at \"{targetSkill.SkillEndpoint}\" (status is {response.Status}). \r\n {response.Body}");
            }
        }

        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            // Try to get the active skill
            var activeSkill = await _activeSkillProperty.GetAsync(turnContext, () => null, cancellationToken);

            if (activeSkill != null)
            {
                // Send the activity to the skill
                await SendToSkill(turnContext, activeSkill, cancellationToken);
                return;
            }

            var text = turnContext.Activity.Text;

            if (!string.IsNullOrEmpty(text))
            {
                BotFrameworkSkill targetSkill = null;
                if (_skillsConfig.Skills.TryGetValue(text, out targetSkill))
                {
                    await turnContext.SendActivityAsync(MessageFactory.Text($"Got it, connecting you to the {text} skill..."), cancellationToken);

                    // Save active skill in state
                    await _activeSkillProperty.SetAsync(turnContext, targetSkill, cancellationToken);

                    // Send the activity to the skill
                    await SendToSkill(turnContext, targetSkill, cancellationToken);
                    return;
                }
            }

            // just respond with choices
            await turnContext.SendActivityAsync(MessageFactory.Attachment(GetOptionsAttachment()), cancellationToken);

            // Save conversation state
            await _conversationState.SaveChangesAsync(turnContext, force: true, cancellationToken: cancellationToken);
        }

        protected override async Task OnEndOfConversationActivityAsync(ITurnContext<IEndOfConversationActivity> turnContext, CancellationToken cancellationToken)
        {
            // forget skill invocation
            await _activeSkillProperty.DeleteAsync(turnContext, cancellationToken);

            // Show status message, text and value returned by the skill
            var eocActivityMessage = $"Received {ActivityTypes.EndOfConversation}.\n\nCode: {turnContext.Activity.Code}";
            if (!string.IsNullOrWhiteSpace(turnContext.Activity.Text))
            {
                eocActivityMessage += $"\n\nText: {turnContext.Activity.Text}";
            }

            if ((turnContext.Activity as Activity)?.Value != null)
            {
                eocActivityMessage += $"\n\nValue: {JsonConvert.SerializeObject((turnContext.Activity as Activity)?.Value)}";
            }

            await turnContext.SendActivityAsync(MessageFactory.Text(eocActivityMessage), cancellationToken);

            // We are back at the root
            await turnContext.SendActivityAsync(MessageFactory.Text("Back in the root dotnet bot."), cancellationToken);
            await turnContext.SendActivityAsync(MessageFactory.Attachment(GetOptionsAttachment()), cancellationToken);


            // Save conversation state
            await _conversationState.SaveChangesAsync(turnContext, cancellationToken: cancellationToken);
        }

        protected override async Task OnEventAsync(ITurnContext<IEventActivity> turnContext, CancellationToken cancellationToken)
        {
            if(turnContext.Activity.Name == "webchat/join")
            {
                await turnContext.SendActivityAsync(MessageFactory.Attachment(GetOptionsAttachment()), cancellationToken);
            }
            await base.OnEventAsync(turnContext, cancellationToken);
        }
    }
}