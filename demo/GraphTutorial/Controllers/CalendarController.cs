// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.Resource;
using Microsoft.Graph;
using TimeZoneConverter;
using GraphTutorial.Models;

namespace GraphTutorial.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [Authorize]
    public class CalendarController : ControllerBase
    {
        private static readonly string[] apiScopes = new[] { "access_as_user" };

        private readonly GraphServiceClient _graphClient;
        private readonly ITokenAcquisition _tokenAcquisition;
        private readonly ILogger<CalendarController> _logger;

        public CalendarController(ITokenAcquisition tokenAcquisition, GraphServiceClient graphClient, ILogger<CalendarController> logger)
        {
            _tokenAcquisition = tokenAcquisition;
            _graphClient = graphClient;
            _logger = logger;
        }

        // <GetSnippet>
        [HttpGet]
        public async Task<IEnumerable<Event>> Get()
        {
            // This verifies that the access_as_user scope is
            // present in the bearer token, throws if not
            HttpContext.VerifyUserHasAnyAcceptedScope(apiScopes);

            // To verify that the identity libraries have authenticated
            // based on the token, log the user's name
            _logger.LogInformation($"Authenticated user: {User.GetDisplayName()}");

            try
            {
                // Get the user's mailbox settings
                var me = await _graphClient.Me
                    .Request()
                    .Select(u => new {
                        u.MailboxSettings
                    })
                    .GetAsync();

                // Get the start and end of week in user's time
                // zone
                var startOfWeek = GetUtcStartOfWeekInTimeZone(
                    DateTime.Today, me.MailboxSettings.TimeZone);
                var endOfWeek = startOfWeek.AddDays(7);

                // Set the start and end of the view
                var viewOptions = new List<QueryOption>
                {
                    new QueryOption("startDateTime", startOfWeek.ToString("o")),
                    new QueryOption("endDateTime", endOfWeek.ToString("o"))
                };

                // Get the user's calendar view
                var results = await _graphClient.Me
                    .CalendarView
                    .Request(viewOptions)
                    // Send user time zone in request so date/time in
                    // response will be in preferred time zone
                    .Header("Prefer", $"outlook.timezone=\"{me.MailboxSettings.TimeZone}\"")
                    // Get max 50 per request
                    .Top(50)
                    // Only return fields app will use
                    .Select(e => new
                    {
                        e.Subject,
                        e.Organizer,
                        e.Start,
                        e.End,
                        e.Location
                    })
                    // Order results chronologically
                    .OrderBy("start/dateTime")
                    .GetAsync();

                return results.CurrentPage;
            }
            catch (Exception ex)
            {
                await HandleGraphException(ex);
                return null;
            }
        }
        // </GetSnippet>

        // <GetStartOfWeekSnippet>
        private DateTime GetUtcStartOfWeekInTimeZone(DateTime today, string timeZoneId)
        {
            // Time zone returned by Graph could be Windows or IANA style
            // TimeZoneConverter can take either
            TimeZoneInfo userTimeZone = TZConvert.GetTimeZoneInfo(timeZoneId);

            // Assumes Sunday as first day of week
            int diff = System.DayOfWeek.Sunday - today.DayOfWeek;

            // create date as unspecified kind
            var unspecifiedStart = DateTime.SpecifyKind(today.AddDays(diff), DateTimeKind.Unspecified);

            // convert to UTC
            return TimeZoneInfo.ConvertTimeToUtc(unspecifiedStart, userTimeZone);
        }
        // </GetStartOfWeekSnippet>

        // <HandleGraphExceptionSnippet>
        private async Task HandleGraphException(Exception exception)
        {
            if (exception is MicrosoftIdentityWebChallengeUserException)
            {
                _logger.LogError(exception, "Consent required");
                // This exception indicates consent is required.
                // Return a 403 with "consent_required" in the body
                // to signal to the tab it needs to prompt for consent
                HttpContext.Response.ContentType = "text/plain";
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await HttpContext.Response.WriteAsync("consent_required");
            }
            else if (exception is ServiceException)
            {
                var serviceException = exception as ServiceException;
                _logger.LogError(serviceException, "Graph service error occurred");
                HttpContext.Response.ContentType = "text/plain";
                HttpContext.Response.StatusCode = (int)serviceException.StatusCode;
                await HttpContext.Response.WriteAsync(serviceException.Error.ToString());
            }
            else
            {
                _logger.LogError(exception, "Error occurred");
                HttpContext.Response.ContentType = "text/plain";
                HttpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await HttpContext.Response.WriteAsync(exception.ToString());
            }
        }
        // </HandleGraphExceptionSnippet>

        // <PostSnippet>
        [HttpPost]
        public async Task<string> Post(NewEvent newEvent)
        {
            HttpContext.VerifyUserHasAnyAcceptedScope(apiScopes);

            try
            {
                // Get the user's mailbox settings
                var me = await _graphClient.Me
                    .Request()
                    .Select(u => new {
                        u.MailboxSettings
                    })
                    .GetAsync();

                // Create a Graph Event
                var graphEvent = new Event
                {
                    Subject = newEvent.Subject,
                    Start = new DateTimeTimeZone
                    {
                        DateTime = newEvent.Start,
                        TimeZone = me.MailboxSettings.TimeZone
                    },
                    End = new DateTimeTimeZone
                    {
                        DateTime = newEvent.End,
                        TimeZone = me.MailboxSettings.TimeZone
                    }
                };

                // If there are attendees, add them
                if (!string.IsNullOrEmpty(newEvent.Attendees))
                {
                    var attendees = new List<Attendee>();
                    var emailArray = newEvent.Attendees.Split(';');
                    foreach (var email in emailArray)
                    {
                        attendees.Add(new Attendee
                        {
                            Type = AttendeeType.Required,
                            EmailAddress = new EmailAddress
                            {
                                Address = email
                            }
                        });
                    }

                    graphEvent.Attendees = attendees;
                }

                // If there is a body, add it
                if (!string.IsNullOrEmpty(newEvent.Body))
                {
                    graphEvent.Body = new ItemBody
                    {
                        ContentType = BodyType.Text,
                        Content = newEvent.Body
                    };
                }

                // Create the event
                await _graphClient.Me
                    .Events
                    .Request()
                    .AddAsync(graphEvent);

                return "success";
            }
            catch (Exception ex)
            {
                await HandleGraphException(ex);
                return null;
            }
        }
        // </PostSnippet>
    }
}
