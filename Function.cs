using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Util;

using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Amazon.S3.Model;
using System.IO;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace AWSLambdaSES
{
    public class Function
    {
        IAmazonS3 S3Client { get; set; }

        /// <summary>
        /// Default constructor. This constructor is used by Lambda to construct the instance. When invoked in a Lambda environment
        /// the AWS credentials will come from the IAM role associated with the function and the AWS region will be set to the
        /// region the Lambda function is executed in.
        /// </summary>
        public Function()
        {
            S3Client = new AmazonS3Client();
        }

        /// <summary>
        /// Constructs an instance with a preconfigured S3 client. This can be used for testing the outside of the Lambda environment.
        /// </summary>
        /// <param name="s3Client">The preconfigured S3 client to use</param>
        public Function(IAmazonS3 s3Client)
        {
            this.S3Client = s3Client;
        }
        
        /// <summary>
        /// This method is called for every Lambda invocation. This method takes in an S3 event object and can be used 
        /// to respond to S3 notifications.
        /// </summary>
        /// <param name="evnt">The s3Event that triggered the Lambda function</param>
        /// <param name="context">Information about the running Lambda function and CloudWatch logs</param>
        /// <returns>Null if the S3Event does not exist, any errors if they occur, or the content type of a successful response</returns>
        public async Task<string> FunctionHandler(S3Event evnt, ILambdaContext context)
        {
            var s3Event = evnt.Records?[0].S3;
            if(s3Event == null)
            {
                return null;
            }

            try
            {
                //check if the file is a CSV file - if so process emails
                if (s3Event.Object.Key.EndsWith(".csv"))
                {
                    //check that the template exists - if not - return with error
                    //Raw Template name convention: <projectacronym>_v<version#>.txt
                    //HTML Template convention: <projectacronym>_v<version#>.html
                    //CSV file name convention: <project-acronym>_v<version#>.csv
                    if (!s3Event.Object.Key.Contains("_v"))
                    {
                        Console.WriteLine("File name does not conform to standard for email template.");
                        return "CSV File Name Format Error";
                    }
                    //else continue on using the template
                    string htmlTemplate = s3Event.Object.Key.Replace(".csv",".html");
                    string textTemplate = s3Event.Object.Key.Replace(".csv", ".txt");

                    //get the file provided in the S3 bucket
                    using (GetObjectResponse csvFile = await this.S3Client.GetObjectAsync(s3Event.Bucket.Name, s3Event.Object.Key))
                    {
                        //read file and send emails provided in file
                        using(StreamReader strRead = new StreamReader(csvFile.ResponseStream))
                        {
                            //assumes CSV file will NOT have column headers as first row
                            List<string> columnHeaders = new List<string>();
                            int lineCount = 0;

                            //parse all data
                            while (!strRead.EndOfStream)
                            {
                                //get values for current line
                                string currentLine = strRead.ReadLine();
                                List<string> dataCells = currentLine.Split(',').ToList();

                                if (lineCount == 0)
                                {
                                    //SENDER,RECIPIENT,SUBJECT; additional replacement tags - no quotes allowed
                                    columnHeaders = dataCells;
                                }
                                else
                                {
                                    //delimit based on double quotes, then commas
                                    dataCells = parseDelimitedString(currentLine, columnHeaders.Count());

                                    //get basic required fields
                                    string fromAddress = dataCells[columnHeaders.IndexOf("SENDER")];
                                    string toAddress = dataCells[columnHeaders.IndexOf("RECIPIENT")];
                                    string subject = dataCells[columnHeaders.IndexOf("SUBJECT")];
                                    //get text and HTML template files
                                    string textBody = await getFileContents(s3Event.Bucket.Name, textTemplate);
                                    string htmlBody = await getFileContents(s3Event.Bucket.Name, htmlTemplate);

                                    //replace any additional data elements from the csv into the email templates
                                    for(int i = 0; i < dataCells.Count(); i++)
                                    {
                                        subject = subject.Replace("$" + columnHeaders[i], dataCells[i]);
                                        textBody = textBody.Replace("$" + columnHeaders[i], dataCells[i]);
                                        htmlBody = htmlBody.Replace("$" + columnHeaders[i], dataCells[i]);
                                    }

                                    //replace en/em dashes with hyphens
                                    subject = subject.Replace(Char.ConvertFromUtf32(65533).ToString(), "-");
                                    textBody = textBody.Replace(Char.ConvertFromUtf32(65533).ToString(), "-");
                                    htmlBody = htmlBody.Replace(Char.ConvertFromUtf32(65533).ToString(), "-");

                                    //send email
                                    var r3 = await SendTemplateEmail(fromAddress, toAddress, subject, textBody, htmlBody);
                                    if(r3 == null)
                                    {
                                        Console.WriteLine("Email was not sent to: " + toAddress);
                                        return "Error";
                                    }
                                }
                                lineCount++;
                                
                            } //end while
                        } //close stream reader
                    } //close S3 Object Response
                } //end csv handler

                //get the file provided in the S3 bucket
                var response = await this.S3Client.GetObjectMetadataAsync(s3Event.Bucket.Name, s3Event.Object.Key);
                Console.WriteLine("File successfully processed...");

                //note - the max filename is 1024 characters
                //rename file so as not to re-trigger the lambda - use timestamp so as not to overwrite old processed files
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HH-mm-ss");
                var finalize = await renameFileWithinBucket(s3Event.Object.Key, s3Event.Bucket.Name, s3Event.Object.Key + "-processed"+timestamp);

                return response.Headers.ContentType;
            }
            catch(Exception e)
            {
                context.Logger.LogLine($"Error getting object {s3Event.Object.Key} from bucket {s3Event.Bucket.Name}. Make sure they exist and your bucket is in the same region as this function.");
                context.Logger.LogLine(e.Message);
                context.Logger.LogLine(e.StackTrace);
                string errorMessage = "Error in AWS Lambda SES. " + e.Message + e.StackTrace;
                //If you want to email someone when there are errors in yuor Lambda, instead of just adding them to your log, do so here:
                //var r3 = await SendTemplateEmail("REPLACE WITH AWS VERIFIED SENDER ADDRESS", "REPLACE WTIH RECEIVER ADDRESS", "REPLACE WITH SUBJECT STRING", errorMessage, errorMessage);
                throw;
            }
        }
        
        /// <summary>
        /// Reads the contents of a file and returns as a string
        /// </summary>
        /// <param name="fileName">The file to read</param>
        /// <returns>The body of the file as a string</returns>
        public async Task<String> getFileContents(string s3BucketName, string fileName)
        {
            string fileBody = string.Empty;

            using (GetObjectResponse htmlFile = await this.S3Client.GetObjectAsync(s3BucketName, fileName))
            {
                //read file and send emails provided in file
                using (StreamReader htmlStrRead = new StreamReader(htmlFile.ResponseStream))
                {
                    fileBody = htmlStrRead.ReadToEnd();
                }
            }
            return fileBody;
        }

        /// <summary>
        /// Sends an email using the Amazon Simple Email Service (SES) Client
        /// </summary>
        /// <param name="senderAddress">This address must be verified with Amazon SES.</param>
        /// <param name="receiverAddress">This address must be verified with Amazon SES.</param>
        /// <param name="subject">The subject line for the email.</param>
        /// <param name="textBody">The email body for recipients with non-HTML email clients. Text version of the email body.</param>
        /// <param name="htmlBody">The HTML version of the email body.</param>
        /// <returns></returns>
        public async Task<SendEmailResponse> SendTemplateEmail(string senderAddress, string receiverAddress, string subject, string textBody, string htmlBody)
        {
            // The configuration set to use for this email. If you do not want to use a
            // configuration set, comment out the following property and the
            // ConfigurationSetName = configSet argument below. 
            //static readonly string configSet = "ConfigSet";
            
            // (Optional) the name of a configuration set to use for this message.
            // If you comment out this line, you also need to remove or comment out
            // the "X-SES-CONFIGURATION-SET" header below.
            //const String CONFIGSET = "ConfigSet";

            // Fill in with the Amazon SES region endpoint            
            using (var client = new AmazonSimpleEmailServiceClient(Amazon.RegionEndpoint.<ENDPOINT VALUE GOES HERE>))
            {
                var sendRequest = new SendEmailRequest
                {
                    Source = senderAddress,
                    Destination = new Destination
                    {
                        ToAddresses = new List<string> { receiverAddress }
                    },
                    Message = new Message
                    {
                        Subject = new Content(subject),
                        Body = new Body
                        {
                            Html = new Content
                            {
                                Charset = "UTF-8",
                                Data = htmlBody
                            },
                            Text = new Content
                            {
                                Charset = "UTF-8",
                                Data = textBody
                            }
                        }
                    },
                    // If you are not using a configuration set, comment
                    // or remove the following line 
                    //ConfigurationSetName = configSet
                };
                try
                {
                    Console.WriteLine("Sending email using Amazon SES...");
                    SendEmailResponse s = await client.SendEmailAsync(sendRequest); 
                    Console.WriteLine("The email was sent successfully to " + receiverAddress + ", with status code: " + s.HttpStatusCode.ToString());
                    return s;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("The email was not sent to: " + receiverAddress);
                    Console.WriteLine("Error message: " + ex.Message);
                }
            } //end of using block
            return null;
        }

        /// <summary>
        /// Renames a file from the sourceBucket to the new destination File Name
        /// </summary>
        /// <param name="fileNameToBackup">The s3Event.Object.Key to backup</param>
        /// <param name="sourceBucket">The S3 bucket the fileToBackup is stored in</param>
        /// <param name="destinationFileName">The new file name to be used</param>
        public async Task<DeleteObjectResponse> renameFileWithinBucket(string fileNameToBackup, string sourceBucket, string destinationFileName)
        {
            //can be renamed to a new bucket by changing the copy parameters
            var r2 = await this.S3Client.CopyObjectAsync(sourceBucket, fileNameToBackup, sourceBucket, destinationFileName);
            Console.WriteLine("File renamed via Copy..." + r2.HttpStatusCode);
            var r3 = await this.S3Client.DeleteObjectAsync(sourceBucket, fileNameToBackup);
            Console.WriteLine("Original file removed..." + r3.HttpStatusCode);

            return r3;
        }

        /// <summary>
        /// Parses the provided string into the maximum number of columns
        /// Removes invalid characters for emails (en dash/em dash) and replaces with email friendly alternatives
        /// </summary>
        /// <param name="stringToParse">The string to parse</param>
        /// <param name="cols">The maximum number of columns</param>
        /// <returns>The resulting list of strings</returns>
        public List<string> parseDelimitedString(string stringToParse, int cols)
        {
            List<string> values = new List<string>();
            
            //format for emails
            stringToParse = stringToParse.Trim(' '); //remove starting/ending spaces

            //checks if text is inside quotes, if so, ignores commas inside the quotes, or escaped quotes in the string as well
            values = Regex.Split(stringToParse, "(\"(?:[^\"]|\"\"|[,])*\"[,]|\"(?:[^\"]|\"\"|[,])*\"|[^,]*[,]|[^,]*)").ToList();
            //Regex splits out empty values and commas as separate data items, remove - not needed
            values.RemoveAll(IsEmptyString);

            //clean up split out list
            for(int i = 0; i< values.Count(); i++)
            {
                //remove any trailing commas (from the csv) and any leading/trailing spaces (from the data)
                values[i] = values[i].TrimEnd(',').Trim(' ');
                //for quote delimited strings - remove the quotes
                if (values[i].StartsWith("\"") && values[i].EndsWith("\""))
                {
                    values[i] = values[i].Trim('"');
                }
            }

            //if there were extra null values at the end - make sure we keep our list in sync with the column headers
            if (values.Count() < cols)
            {
                values.AddRange(new string[cols - values.Count()]);
            }

            return values;
        }

        /// <summary>
        /// Checks if the provided value is an empty string
        /// </summary>
        /// <param name="s">The string to check</param>
        /// <returns>True if an empty string; false if not</returns>
        private static bool IsEmptyString(String s)
        {
            return s.Equals(string.Empty);
        }
    }
}
