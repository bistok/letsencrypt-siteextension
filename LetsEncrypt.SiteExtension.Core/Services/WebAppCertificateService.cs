﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LetsEncrypt.Azure.Core;
using LetsEncrypt.Azure.Core.Models;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.Management.WebSites;
using System.Diagnostics;
using System.Net.Http;
using Newtonsoft.Json;
using Polly;

namespace LetsEncrypt.Azure.Core.Services
{
    /// <summary>
    /// Installs and assigns the certificate directly to the app service plan. 
    /// </summary>
    public class WebAppCertificateService : ICertificateService
    {
        private readonly IAzureWebAppEnvironment azureEnvironment;
        private readonly IWebAppCertificateSettings settings;

        public WebAppCertificateService(IAzureWebAppEnvironment azureEnvironment, IWebAppCertificateSettings settings)
        {
            this.azureEnvironment = azureEnvironment;
            this.settings = settings;
        }
        public async Task Install(ICertificateInstallModel model)
        {
            var cert = model.CertificateInfo;
            using (var webSiteClient = await ArmHelper.GetWebSiteManagementClient(azureEnvironment))
            {

                var s = webSiteClient.WebApps.GetSiteOrSlot(azureEnvironment.ResourceGroupName, azureEnvironment.WebAppName, azureEnvironment.SiteSlotName);

                Trace.TraceInformation(String.Format("Installing certificate {0} on azure with server farm id {1}", cert.Name, s.ServerFarmId));
                var newCert = new Certificate(s.Location, cert.Password, name: model.Host + "-" + cert.Certificate.Thumbprint, pfxBlob: cert.PfxCertificate, serverFarmId: s.ServerFarmId);
                //BUG https://github.com/sjkp/letsencrypt-siteextension/issues/99
                //using this will not install the certificate with the correct webSpace property set, 
                //and the app service will be unable to find the certificate if the app service plan has been moved between resource groups.
                //webSiteClient.Certificates.CreateOrUpdate(azureEnvironment.ServicePlanResourceGroupName, cert.Certificate.Subject.Replace("CN=", ""), newCert);

                var client = await ArmHelper.GetHttpClient(azureEnvironment);

                var body = JsonConvert.SerializeObject(newCert, JsonHelper.DefaultSerializationSettings);

                var retryPolicy = ArmHelper.ExponentialBackoff();

                var t = await retryPolicy.ExecuteAsync(async () =>
                {
                     return await client.PutAsync($"/subscriptions/{azureEnvironment.SubscriptionId}/resourceGroups/{azureEnvironment.ServicePlanResourceGroupName}/providers/Microsoft.Web/certificates/{newCert.Name}?api-version=2016-03-01", new StringContent(body, Encoding.UTF8, "application/json"));                    
                });
                Trace.TraceInformation(await t.Content.ReadAsStringAsync());
                t.EnsureSuccessStatusCode();

                foreach (var dnsName in model.AllDnsIdentifiers)
                {
                    var sslState = s.HostNameSslStates.FirstOrDefault(g => g.Name == dnsName);

                    if (sslState == null)
                    {
                        sslState = new HostNameSslState()
                        {
                            Name = dnsName,
                            SslState = settings.UseIPBasedSSL ? SslState.IpBasedEnabled : SslState.SniEnabled,
                        };
                        s.HostNameSslStates.Add(sslState);
                    }
                    else
                    {
                        //First time setting the HostNameSslState it is set to disabled.
                        sslState.SslState = settings.UseIPBasedSSL ? SslState.IpBasedEnabled : SslState.SniEnabled;
                    }
                    sslState.ToUpdate = true;
                    sslState.Thumbprint = cert.Certificate.Thumbprint;
                }
                webSiteClient.WebApps.BeginCreateOrUpdateSiteOrSlot(azureEnvironment.ResourceGroupName, azureEnvironment.WebAppName, azureEnvironment.SiteSlotName, s);
            }


        }        

        public async Task<List<string>> RemoveExpired(int removeXNumberOfDaysBeforeExpiration = 0)
        {
            using (var webSiteClient = await ArmHelper.GetWebSiteManagementClient(azureEnvironment))
            {
                var certs = webSiteClient.Certificates.ListByResourceGroup(azureEnvironment.ServicePlanResourceGroupName);
                var site = webSiteClient.WebApps.GetSiteOrSlot(azureEnvironment.ResourceGroupName, azureEnvironment.WebAppName, azureEnvironment.SiteSlotName);
                
                var tobeRemoved = certs.Where(s => s.ExpirationDate < DateTime.UtcNow.AddDays(removeXNumberOfDaysBeforeExpiration) && (s.Issuer.Contains("Let's Encrypt") || s.Issuer.Contains("Fake LE")) && !site.HostNameSslStates.Any(hostNameBindings => hostNameBindings.Thumbprint == s.Thumbprint)).ToList();
                foreach (var cert in tobeRemoved)
                {
                    await RemoveCertificate(webSiteClient, cert);
                }

                return tobeRemoved.Select(s => s.Thumbprint).ToList();
            }
        }

        private async Task RemoveCertificate(WebSiteManagementClient webSiteClient,  Certificate s)
        {            
            await webSiteClient.Certificates.DeleteAsync(azureEnvironment.ServicePlanResourceGroupName, s.Name);
        }

        
    }
}
