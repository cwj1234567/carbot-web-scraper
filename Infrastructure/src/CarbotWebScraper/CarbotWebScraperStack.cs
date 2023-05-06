using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.IAM;
using Constructs;
using Schedule = Amazon.CDK.AWS.ApplicationAutoScaling.Schedule;

namespace CarbotWebScraper
{
    public class CarbotWebScraperStack : Stack
    {
        internal CarbotWebScraperStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // Create a VPC
            var vpc = new Vpc(this, "carbot-scraper-vpc", new VpcProps
            {
                MaxAzs = 2
            });

            // Create an ECS cluster
            var cluster = new Cluster(this, "carbot-scraper-cluster", new ClusterProps
            {
                Vpc = vpc
            });

            // Define the ECR repository
            var ecrRepository = Repository.FromRepositoryName(this, "WebScraperRepo", "carbot-scraper");

            // three different tasks for each site
            var scraperTasks = new List<string> {"cab", "ebay", "bat"};


            // Iterate through envVars and create task definitions, services, and security groups
            foreach (var task in scraperTasks)
            {
                // Create a task definition
                var taskDefinition = new FargateTaskDefinition(this, $"scraper-task-{task}",
                    new FargateTaskDefinitionProps
                    {
                        MemoryLimitMiB = 1024,
                        Cpu = 256
                    });

                // Add the Selenium container
                taskDefinition.AddContainer($"SeleniumContainer", new ContainerDefinitionOptions
                {
                    Image = ContainerImage.FromRegistry("selenium/standalone-chrome:4.0.0"),
                    MemoryLimitMiB = 512
                });

                // Add the WebScraper container
                var webscraperContainer = taskDefinition.AddContainer($"WebScraperContainer",
                    new ContainerDefinitionOptions
                    {
                        Image = ContainerImage.FromEcrRepository(ecrRepository),
                        MemoryLimitMiB = 512,
                        Environment = new Dictionary<string, string>
                        {
                            { "SCRAPER_SERVICE", task }
                        }
                    });

                // Grant the task definition the required permissions
                taskDefinition.TaskRole.AddManagedPolicy(
                    ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"));
                taskDefinition.TaskRole.AddManagedPolicy(
                    ManagedPolicy.FromAwsManagedPolicyName("AWSXRayDaemonWriteAccess"));


                // Create a Fargate service
                var service = new FargateService(this, $"scraper-service-{task}", new FargateServiceProps
                {
                    Cluster = cluster,
                    TaskDefinition = taskDefinition,
                    DesiredCount = 1,
                    AssignPublicIp = true,
                    VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
                });


                // Create a security group and add the required ingress rules
                var securityGroup = new SecurityGroup(this, $"scraper-sg-{task}", new SecurityGroupProps
                {
                    Vpc = vpc,
                    AllowAllOutbound = true
                });
                
                // Create a scheduled Fargate task
                var scheduledTask = new ScheduledFargateTask(this, $"scheduled-task-{task}", new ScheduledFargateTaskProps
                {
                    Cluster = cluster,
                    ScheduledFargateTaskDefinitionOptions = new ScheduledFargateTaskDefinitionOptions
                    {
                        TaskDefinition = taskDefinition,
                    },
                    SubnetSelection = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
                    Schedule = Schedule.Cron(new Amazon.CDK.AWS.ApplicationAutoScaling.CronOptions(){Day = "*",Hour = "0",Minute = "0",Month = "*"}),
                    RuleName = $"scheduled-task-rule-{task}"
                });

                
                
            }
        }
    }
}
