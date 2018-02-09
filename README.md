# bulk-mail-surveys-aws-s3-lambda-ses
Bulk Mail for Surveys w/ AWS S3, Lambda, and SES

What is it? A cloud-based serverless application using AWS Lambda, SES, and S3.

How does it work?
1. Create an HTML and TXT mail template with tokens, using:
	- the naming convention: <project acronym>_v<version#>.txt, <project acronym>_v<version#>.html, <project acronym>_v<version#>.csv
	- a dollar sign ($) prefix, and the unique token name, with no spaces.
	- the CSV file columns: SENDER, RECIPIENT, SUBJECT, <Token1>, <Token2>, <Token3>...
2. Populate a CSV template with recipient e-mail addresses and columns with data for token replacement
3. Ensure the SENDER email address is an AWS Verified email address
4. Upload the CSV and mail template files are upload to S3 - which triggers a Lambda function written in C# that processes each row in the CSV and sends an e-mail using SES.  

Configuration/Setup:
1. Install Amazon SDK, Toolkit for Visual Studio
2. Update the aws-lambda-tools-defaults.json file, to provide the following items:
	- profile - update to be the username of who can publish the Lambda function
	- region - update to be the AWS Region to publish the lambda to
	- function-name - update to be your unique Lambda function name
	- function-role - update to be the arn of your IAM Role that has access to Lambda, S3, and SES
3. Update the Function.cs file, to provide the appropriate region (<ENDPOINT VALUE GOES HERE>)

Future Enhancements needed:
- Way to handle images/styles/attachments
- Way to schedule/queue emails
- Better way to handle special characters

References:
This was created using the AWS Lambda Project with Tests (.NET Core) project template as the base.
Publish using AWS Toolkit: https://docs.aws.amazon.com/toolkit-for-visual-studio/latest/user-guide/lambda-creating-project-in-visual-studio.html