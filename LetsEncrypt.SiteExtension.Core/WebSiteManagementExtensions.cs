﻿using Microsoft.Azure.Management.WebSites.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LetsEncrypt.Azure.Core
{
    public static class WebSiteManagementClientExtensions
    {
        public static string ServerFarmResourceGroup(this Site site)
        {
            return ServerResourceGroupFromServerFarmId(site.ServerFarmId);
        }
        
        public static string ServerResourceGroupFromServerFarmId(string serverFarmId)
        {
            var r = new Regex("/resourceGroups/(.*)/providers/Microsoft.Web/");
            var m = r.Match(serverFarmId);            
            return m.Groups[1].Value;
        }

        public static string ServerFarmName(this Site site)
        {
            return ServerFarmNameFromServerFarmId(site.ServerFarmId);
        }

        public static string ServerFarmNameFromServerFarmId(string serverFarmId)
        {
            return serverFarmId.Split(new[] { '/' }).Last();
        }
    }
}
