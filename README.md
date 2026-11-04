# RouteGo Webhook Receiver

AWS Lambda behind an API Gateway HTTP API (`POST /webhooks/payment`). It verifies the payment gateway's
HMAC-SHA256 signature and puts the raw body on SQS `payment-webhooks-q`. No business logic: Payment decides
what the event means. Specs: `12-webhook-receiver.md`, `00-overall-architecture.md`.

| Result | Meaning |
|---|---|
| `200 {"received":true}` | Verified and queued |
| `401` | Signature missing or wrong (log line `signature_invalid`), or event older than the replay window |
| `400` | Correctly signed but no `gatewayEventId` in the body |
| `500` | Secret or queue unavailable; the gateway should retry |

## Configuration (environment variables)

| Name | Required | Meaning |
|---|---|---|
| `SECRET_ID` | yes | Secrets Manager secret holding JSON `{"webhookSecret": "..."}`; cached 5 minutes |
| `QUEUE_URL` | yes | `payment-webhooks-q` URL |
| `SIGNATURE_HEADER` | no | Header carrying the signature, default `X-Signature` (`sha256=<hex>`) |
| `REPLAY_WINDOW_SECONDS` | no | Reject events whose `occurredAt` is older than this; `0`/unset = off |

## Build, test, run

```bash
dotnet build && dotnet test
sam build
sam local invoke WebhookReceiverFunction -e events/payment-success.json --env-vars env.local.json
sam local start-api -p 3000 --env-vars env.local.json
tools/sign.sh body.json          # prints the X-Signature value for a body file
```

Local runs need LocalStack (queue `payment-webhooks-q`, secret `routego/payment/gateway`); see section 12 of the
architecture file. Deploy: `sam build && sam deploy --guided` (stack `routego-lambdas`).
