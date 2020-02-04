// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.AspNetCore.Mvc;

namespace StreamingBotHttpProxy.Controllers
{
    [ApiController]
    public class ProxyController : ControllerBase
    {
        [HttpGet]
        [Route("api/messages")]
        public IActionResult Get()
        {
            return Ok();
        }
    }
}
