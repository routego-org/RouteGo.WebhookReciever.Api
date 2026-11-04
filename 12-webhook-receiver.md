# 12. Webhook Receiver (Lambda #1)

| | |
|---|---|
| **Type** | AWS Lambda behind API Gateway (HTTP API) |
| **Build phase** | 6 (built together with Payment) |
| **Data** | None. Output goes to SQS `payment-webhooks-q` |
| **Runs** | Serverless (no VPC, no EC2) |
| **Repo path** | `lambdas/webhook-receiver` (SAM stack `routego-lambdas`) |

---

## 1. BRD: why it exists
- Payment gateways report results by calling a public URL. They retry only a few times and then give up (BO-3: never lose a payment).
- Our EC2 services are redeployed often. A webhook that lands on a restarting service is a lost payment.
- Webhook traffic is tiny and bursty.

**Business outcome:** the public payment endpoint is always up, whatever state our servers are in.

## 2. PRD: what it offers
| Capability | Behavior |
|---|---|
| Public endpoint | `POST https://<api-id>.execute-api.<region>.amazonaws.com/webhooks/payment` |
| Authenticity check | Only correctly signed requests are accepted |
| Fast acknowledge | Returns `200` as soon as the event is safely queued |

Rules:
- It does **no business logic**: verify, enqueue, answer. Payment decides what the event means.
- A bad signature gets `401` and nothing is queued.
- If queuing fails, return `5xx` so that the gateway retries.

## 3. FRD: how it behaves

### 3.1 Request handling
1. Read the raw body (bytes exactly as received) and the signature header (for example `X-Signature: sha256=<hex>`; header name depends on the gateway).
2. Load the webhook secret from Secrets Manager `routego/payment/gateway` (field `webhookSecret`). Fetch once per container and cache in a static field (cold start cost once; refresh every 5 minutes so that a rotated secret is picked up).
3. Compute HMAC-SHA256 of the raw body; compare with a **constant-time** comparison; reject on mismatch (`401`).
4. Optionally reject if a timestamp in the payload is older than 5 minutes (replay window).
5. `SendMessage` to `payment-webhooks-q` with the raw body as message body and message attributes `gatewayEventId`, `receivedAt`.
6. Return `200 {"received":true}`. If `SendMessage` throws, return `500`.

### 3.2 Settings
| Setting | Value |
|---|---|
| Runtime | Newest managed .NET runtime in your region (check the Lambda runtimes page) |
| Memory / timeout | 256 MB / 5 s |
| Reserved concurrency | e.g. 10, to protect the queue and cost from a flood |
| Environment | `SECRET_ID=routego/payment/gateway`, `QUEUE_URL=<payment-webhooks-q url>` |
| API Gateway | HTTP API, route `POST /webhooks/payment`, throttling (for example 20 rps burst 40) |
| Execution role | `secretsmanager:GetSecretValue` on that one secret (and `kms:Decrypt`); `sqs:SendMessage` on `payment-webhooks-q`; CloudWatch Logs |

### 3.3 SAM template (essentials)
```yaml
AWSTemplateFormatVersion: '2010-09-09'
Transform: AWS::Serverless-2016-10-31
Parameters:
  WebhookQueueUrl: { Type: String }
  WebhookQueueArn: { Type: String }
  GatewaySecretArn: { Type: String }
Resources:
  WebhookReceiverFunction:
    Type: AWS::Serverless::Function
    Properties:
      CodeUri: lambdas/webhook-receiver/
      Handler: WebhookReceiver::WebhookReceiver.Function::Handler
      Runtime: dotnet8        # replace with the current supported .NET runtime id
      MemorySize: 256
      Timeout: 5
      ReservedConcurrentExecutions: 10
      Environment:
        Variables:
          SECRET_ID: !Ref GatewaySecretArn
          QUEUE_URL: !Ref WebhookQueueUrl
      Policies:
        - Statement:
            - Effect: Allow
              Action: secretsmanager:GetSecretValue
              Resource: !Ref GatewaySecretArn
            - Effect: Allow
              Action: sqs:SendMessage
              Resource: !Ref WebhookQueueArn
      Events:
        Webhook:
          Type: HttpApi
          Properties:
            Path: /webhooks/payment
            Method: POST
Outputs:
  WebhookUrl:
    Value: !Sub "https://${ServerlessHttpApi}.execute-api.${AWS::Region}.amazonaws.com/webhooks/payment"
```
(Confirm the implicit HttpApi logical name and the runtime id against the SAM docs when you build it; add KMS permission if the secret uses a customer-managed key.)

### 3.4 Why Lambda here (and not a container)
| Reason | Explanation |
|---|---|
| Availability independent of our servers | Survives EC2 restarts, Kubernetes redeploys, and Compose changes |
| Shape of the work | Stateless, tiny, bursty |
| Cost | Pay per request; zero when idle |
| Boundary | Verifies authenticity at the edge, so unauthenticated traffic never reaches Payment |

### 3.5 Events
Produces messages for Payment (not an SNS event). Consumes HTTP requests from the gateway.

## 4. Dependencies
**Calls:** Secrets Manager (read), SQS (send).
**Called by:** the payment gateway (external).
**Depended on by:** Payment (consumes the queue).

## 5. AWS services used and why
| AWS service | Use | Without it |
|---|---|---|
| **API Gateway (HTTP API)** | Public HTTPS endpoint, throttling, access logs | You expose an EC2 instance and manage TLS and DDoS limits yourself |
| **Lambda** | Verify and enqueue | The endpoint is down whenever the cluster is |
| **SAM** | Template, local invoke, deploy | Click-ops; no repeatable deployment |
| **Secrets Manager** | Webhook signing secret | Secret in env vars or code |
| **SQS** | Durable handoff | Event lost if Payment is down; or Lambda must wait for Payment |
| **IAM execution role** | Two permissions only | A bug in the function could read or write anything |
| **CloudWatch** | Lambda logs, 4xx/5xx metrics, `Errors` alarm | Bad-signature storms and failures unseen |

**What students learn:** SAM (template, `sam build`, `sam local`, `sam deploy`), Lambda execution role vs resource policy, API Gateway integration, cold starts and secret caching, reserved concurrency, and where serverless fits.

## 6. Before building: AWS setup
1. Secrets Manager `routego/payment/gateway` exists (file 06).
2. Queue `payment-webhooks-q` + DLQ exist; note its URL and ARN.
3. SAM CLI and Docker installed locally; AWS credentials for the deployer user (not the instance roles).
4. Decide the gateway signature header name and algorithm from the gateway's documentation (the MockGateway uses `X-Signature: sha256=<hex>`).
5. CloudWatch alarms: Lambda `Errors` greater than 0; API `5xx`; optional metric filter on log lines `signature_invalid`.

## 7. Local development
1. Create `events/payment-success.json`: an API Gateway HTTP API v2 event with body, headers and a valid signature (a helper script `tools/sign.sh` calculates it using the local secret).
2. Local env file `env.local.json`:
   ```json
   { "WebhookReceiverFunction": { "SECRET_ID": "routego/payment/gateway", "QUEUE_URL": "http://host.docker.internal:4566/000000000000/payment-webhooks-q", "AWS_ENDPOINT_URL": "http://host.docker.internal:4566" } }
   ```
   (On Linux, `host.docker.internal` needs `--add-host` or the Docker network; check the SAM docs for your platform. The function code must honor the endpoint URL setting.)
3. Run once:
   ```bash
   sam build
   sam local invoke WebhookReceiverFunction -e events/payment-success.json --env-vars env.local.json
   ```
4. Run as an HTTP endpoint for the MockGateway: `sam local start-api -p 3000 --env-vars env.local.json`.
5. Test cases: valid signature (`200`, message in queue), wrong signature (`401`, no message), missing header (`401`), queue permission denied (`500`).

## 8. Deploying to AWS
1. Prerequisites above.
2. `sam build && sam deploy --guided` with parameters (queue URL and ARN, secret ARN). Stack name `routego-lambdas`.
3. Copy the `WebhookUrl` output; configure it in the gateway (or MockGateway on edge).
4. Verify:
   - Send a signed test request with `curl` (use the signing helper): `200` and one message in `payment-webhooks-q` (Payment consumes it).
   - Send a bad signature: `401`, logs show `signature_invalid`.
   - Complete a test payment end to end.
5. **Break it on purpose:**
   - **Stop Payment and the whole Compose stack** on `routego-secure`, pay in MockGateway: the Lambda still answers `200`; start Payment and the payment is processed from the queue. Compare with an EC2-hosted endpoint that is down.
   - **Replay the webhook 5 times:** five `200`s; Payment records one effect (idempotency is Payment's job).
   - Remove `sqs:SendMessage` from the role: the Lambda returns `500`; the gateway retries; restore and the retries succeed.
   - Rotate the webhook secret in Secrets Manager: show the 5-minute cache refresh and the brief window of `401`s; discuss accepting both old and new during rotation.
   - Set throttling to 1 rps and show `429` from API Gateway under load.

## 9. Lab checklist
- [ ] Endpoint stays up while all EC2 services are stopped.
- [ ] Invalid signature never reaches the queue.
- [ ] Role has exactly two permission statements.
- [ ] `sam deploy` is repeatable from the pipeline in phase 10.
