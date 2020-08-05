using System.Collections.Generic;
using Amazon.Lambda.Core;
using System.Diagnostics;
using System;
using Amazon.Lex;
using Amazon;
using Amazon.Lex.Model;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using System.Text;
using System.Net;
using System.Text.Json;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace GetService
{
    public class Function
    {
        private static readonly RegionEndpoint primaryRegion = RegionEndpoint.EUWest2;
        private static readonly String secretName = "nbcGlobal";
        private static readonly String secretAlias = "AWSCURRENT";

        private static String caseReference;
        private static String taskToken;
        private static String cxmEndPoint;
        private static String cxmAPIKey;
        private Secrets secrets = null;


        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            if (await GetSecrets())
            {
                String instance = Environment.GetEnvironmentVariable("instance");
                JObject o = JObject.Parse(input.ToString());
                caseReference = (string)o.SelectToken("CaseReference");
                taskToken = (string)o.SelectToken("TaskToken");
                switch (instance.ToLower())
                {
                    case "live":
                        cxmEndPoint = secrets.cxmEndPointLive;
                        cxmAPIKey = secrets.cxmAPIKeyLive;
                        String cxmServiceAreaLive = await GetCaseDetailsAsync();
                        if(await UpdateCaseDetailsAsync(cxmServiceAreaLive))
                        {
                            await SendSuccessAsync();
                        }                      
                        break;
                    case "test":
                        cxmEndPoint = secrets.cxmEndPointTest;
                        cxmAPIKey = secrets.cxmAPIKeyTest;
                        String cxmServiceAreaTest = await GetCaseDetailsAsync();
                        if (await UpdateCaseDetailsAsync(cxmServiceAreaTest))
                        {
                            await SendSuccessAsync();
                        }
                        break;
                    default:
                        await SendFailureAsync("Instance not Live or Test : " + instance.ToLower(), "Lambda Parameter Error");
                        Console.WriteLine("ERROR : Instance not Live or Test : " + instance.ToLower());
                        break;
                }
            }
        }

        private async Task<String> GetCaseDetailsAsync()
        {
            String cxmServiceArea = null;

            CaseDetails caseDetails = await GetCustomerContactAsync(cxmEndPoint, cxmAPIKey, caseReference, taskToken);
            try
            {
                if (!String.IsNullOrEmpty(caseDetails.customerContact))
                {
                    String response = await GetIntentFromLexAsync(caseDetails.customerContact);

                    if (!String.IsNullOrEmpty(response))
                    {
                        Console.WriteLine("Service : " + response);
                        switch(response){
                            case "ContactNBC":
                                cxmServiceArea = "contact_nbc_councillor_enquiries";
                                break;
                            case "CustomerFeedback_NBC":
                                cxmServiceArea = "customer_feedback_nbc_feedback";
                                break;
                            case "EnvironmentalHealth":
                                cxmServiceArea = "environmental_health";
                                break;
                            case "Events":
                                cxmServiceArea = "events";
                                break;
                            case "GeneralEnquiries":
                                cxmServiceArea = "general_enquiries";
                                break;
                            case "HousingCustomerServices":
                                cxmServiceArea = "housing_customer_services";
                                break;
                            case "NPHCustomerFeedback":
                                cxmServiceArea = "nph_customer_feedback";
                                break;
                            case "Repairs":
                                cxmServiceArea = "housing_repairs";
                                break;
                            case "Streetcare":
                                cxmServiceArea = "streetcare_services_waste_and_recycling_grounds_maintenance";
                                break;
                            default:
                                cxmServiceArea = "general_enquiries";
                                await SendFailureAsync("Unexpected Intent Returned : " + response, "GetCaseDetailsAsync");
                                Console.WriteLine("ERROR : Unexpected Intent Returned : " + response);
                                break;
                        }
                    }
                    else
                    {
                        cxmServiceArea = "general_enquiries";
                    }
                }
            }
            catch (Exception error)
            {
                cxmServiceArea = "general_enquiries";
                await SendFailureAsync(error.Message, "GetCaseDetailsAsync");
                Console.WriteLine("ERROR : GetCaseDetailsAsync :  " + error.Message);
            }
            return cxmServiceArea;
        }

        private async Task<Boolean> UpdateCaseDetailsAsync(String serviceArea)
        {
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            String uri = "/api/service-api/norbert/case/" + caseReference + "/edit?key=" + cxmAPIKey;
            Dictionary<string, string> cxmPayload = new Dictionary<string, string>
            {
                { "service-area", serviceArea }
            };
            String json = JsonConvert.SerializeObject(cxmPayload, Formatting.Indented);
            StringContent content = new StringContent(json);

            HttpResponseMessage response = await cxmClient.PatchAsync(uri, content);

            try
            {
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync(error.Message, "UpdateCaseDetailsAsync");
                Console.WriteLine("ERROR : UpdateCaseDetailsAsync :  " + error.Message);
                return false;
            }
        }

        private async Task<Boolean> GetSecrets()
        {
            IAmazonSecretsManager client = new AmazonSecretsManagerClient(primaryRegion);

            GetSecretValueRequest request = new GetSecretValueRequest();
            request.SecretId = secretName;
            request.VersionStage = secretAlias;

            try
            {
                GetSecretValueResponse response = await client.GetSecretValueAsync(request);
                secrets = JsonConvert.DeserializeObject<Secrets>(response.SecretString);
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync("GetSecrets", error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.StackTrace);
                return false;
            }
        }


        private async Task<CaseDetails> GetCustomerContactAsync(String cxmEndPoint, String cxmAPIKey, String caseReference, String taskToken)
        {
            CaseDetails caseDetails = new CaseDetails();
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            String requestParameters = "key=" + cxmAPIKey;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/service-api/norbert/case/" + caseReference + "?" + requestParameters);
            try
            {
                HttpResponseMessage response = cxmClient.SendAsync(request).Result;
                if (response.IsSuccessStatusCode)
                {
                    HttpContent responseContent = response.Content;
                    String responseString = responseContent.ReadAsStringAsync().Result;
                    JObject caseSearch = JObject.Parse(responseString);
                    caseDetails.customerContact = (String)caseSearch.SelectToken("values.enquiry_details");
                }
                else
                {
                    await SendFailureAsync("Getting case details for " + caseReference, response.StatusCode.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + request.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + response.StatusCode.ToString());
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync("Getting case details for " + caseReference,error.Message);
                Console.WriteLine("ERROR : GetStaffResponseAsync : " + error.StackTrace);
            }
            return caseDetails;
        }

        private async Task<string> GetIntentFromLexAsync(String customerContact)
        {
            try
            {
                AmazonLexClient lexClient = new AmazonLexClient(primaryRegion);
                PostTextRequest textRequest = new PostTextRequest();
                textRequest.UserId = "MailBot";
                textRequest.BotAlias = "DEV";
                textRequest.BotName = "NBC_Mailbot_Intents";
                textRequest.InputText = customerContact;
                PostTextResponse textRespone = await lexClient.PostTextAsync(textRequest);
                return textRespone.IntentName;
            }
            catch(Exception error)
            {
                await SendFailureAsync("Getting Intent", error.Message);
                Console.WriteLine("ERROR : GetIntentFromLexAsync : " + error.StackTrace);
                return "GeneralEnquiries";
            }
        }

        private async Task SendSuccessAsync()
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Success"  },
                { "Message" , "Completed"}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.Message);
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        private async Task SendFailureAsync(String failureCause, String failureError)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskFailureRequest failureRequest = new SendTaskFailureRequest();
            failureRequest.Cause = failureCause;
            failureRequest.Error = failureError;
            failureRequest.TaskToken = taskToken;

            try
            {
                await client.SendTaskFailureAsync(failureRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendFailureAsync : " + error.Message);
                Console.WriteLine("ERROR : SendFailureAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

    }
    class CaseDetails
    {
        public String customerContact { get; set; } = "";
    }

    public class Secrets
    {
        public string cxmEndPointTest { get; set; }
        public string cxmEndPointLive { get; set; }
        public string cxmAPIKeyTest { get; set; }
        public string cxmAPIKeyLive { get; set; }
    }
}
