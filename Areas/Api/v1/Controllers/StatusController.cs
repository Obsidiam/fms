﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PikaCore.Areas.Api.v1.Models;
using PikaCore.Areas.Api.v1.Models.DTO;
using PikaCore.Areas.Api.v1.Services;
using PikaCore.Areas.Core.Pages.Admin.DTO;
using PikaCore.Areas.Infrastructure.Data;
using PikaCore.Areas.Infrastructure.Services;

namespace PikaCore.Areas.Api.v1.Controllers
{
    [ApiController]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [Area("Api")]
    [Route("/{area}/v1/status/[action]")]
    public class StatusController : ControllerBase
    {
        private readonly IMessageService _messageService;
        private readonly IStatusService _statusService;
        private readonly ISystemService _systemService;
        
        public StatusController(IMessageService messageService,
                                IStatusService statusService,
                                ISystemService systemService
                                )
        {
            _messageService = messageService;
            _statusService = statusService;
            _systemService = systemService;
        }
        
        [HttpGet]
        [ActionName("index")]
        public async Task<IActionResult> Index()
        {
            var allStatuses = await _statusService.CheckAllSystems();
            var isAllOk = allStatuses.All(m => m.Value);
            var isAnyOk = allStatuses.Any(m => m.Value);
            var apiMsg = new ApiMessage<MessageDto> {Status = isAllOk};
            var overallStatusMessage = isAllOk 
                ? "System is in graceful state." 
                : (isAnyOk ? "System is degraded." : "System is all down.");
            apiMsg.Messages.Push(overallStatusMessage);

            if (isAllOk) return Ok(apiMsg);
            
            var desertedSystems = 
                allStatuses.Where(s => !s.Value).ToList();
            desertedSystems.ForEach(n =>
            {
                apiMsg.Messages.Push($"{n.Key} is in downstate due to maintenance or a critical system fault.");
            });
            return Ok(apiMsg);
        }
        
        [HttpGet]
        [ActionName("{systemName}/messages")]
        [AllowAnonymous]
        public async Task<IActionResult> Messages(string systemName, int order = 0, int count = 10, int offset = 0)
        {
            try
            {
                var messages = await _messageService.GetAllMessages();
                var dtos = new List<MessageDto>();
                messages.Where(m => m.SystemDescriptor.SystemName.Equals(systemName))
                    .ToList()
                    .ForEach(m => dtos.Add(MessageDto.FromMessageEntity(m)));
                if (order == 1)
                {
                    messages = messages.OrderByDescending(m => m.Id).ToList();
                }
                _messageService.ApplyPaging(ref messages, count, offset);
                var apiMessage = new ApiMessage<IList<MessageDto>> {Data = dtos, Status = true};
                apiMessage.Messages.Push("Messages available for current role.");
                return Ok(apiMessage);
            }
            catch (Exception e)
            {
                var apiMessage = new ApiMessage<IList<MessageDto>> { Status = false};
                apiMessage.Messages.Push("The request couldn't be understood.");
                return BadRequest(apiMessage);
            }
        }
        
        [HttpGet]
        [ActionName("messages/filter")]
        public async Task<IActionResult> MessagesByDateCreated([FromQuery]DateTime from, [FromQuery]DateTime to, int order = 0)
        {
            return StatusCode(302, "Temporarily moved to /messages");
        }

        [HttpGet]
        [ActionName("messages/{id}")]
        public async Task<IActionResult> MessageById(int id)
        {
            var apiMessage = new ApiMessage<MessageDto>();
            try
            {
                apiMessage.Data = MessageDto.FromMessageEntity(await _messageService.GetMessageById(id));
                apiMessage.Status = true;
                apiMessage.Messages.Push($"Successfully returned a message of id {id}");
                return Ok(apiMessage);
            }
            catch (Exception e)
            {
                apiMessage.Messages.Push("The server couldn't find requested message.");
                apiMessage.Status = false;
                return StatusCode(StatusCodes.Status404NotFound, apiMessage);
            }
        }

        [HttpGet]
        [ActionName("systems")]
        public async Task<IActionResult> Systems()
        {
            var apiMessage = new ApiMessage<List<string>>();
            try
            {
                var systemNames = await _systemService.GetAllSystemNames();
                apiMessage.Data = systemNames;
                apiMessage.Status = true;
                apiMessage.Messages.Push("All public systems in PikaCloud.");
                return Ok(apiMessage);
            }
            catch (Exception e)
            {
                apiMessage.Status = false;
                apiMessage.Messages.Push(e.Message);
                return StatusCode(StatusCodes.Status500InternalServerError, apiMessage);
            }
        }

        [HttpGet]
        [ActionName("systems/{systemName}/state")]
        public async Task<IActionResult> SystemTextState(string systemName)
        {
            var apiMessage = new ApiMessage<string>();
            try
            {
                var isUp = await _statusService.CheckSpecificSystem(
                    await _systemService.GetDescriptorByName(systemName)
                    );
                apiMessage.Data = isUp ? "Graceful" : "Down";
                apiMessage.Status = true;
                apiMessage.Messages.Push($"Text state for {systemName}");
                return Ok(apiMessage);
            }
            catch (Exception e)
            {
                apiMessage.Status = false;
                apiMessage.Messages.Push(e.Message);
                return StatusCode(StatusCodes.Status500InternalServerError, apiMessage);
            }
        }
    }
}