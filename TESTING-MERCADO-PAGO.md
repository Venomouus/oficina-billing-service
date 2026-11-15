# Guia de teste do Mercado Pago Sandbox

O Billing Service suporta dois fluxos sandbox:

- `payment/preference`: Checkout Pro, que retorna uma URL de checkout.
- `payment/qr`: QR Code dinâmico, alinhado à Orders API da documentação do Mercado Pago.

O QR dinâmico exige que exista uma loja e um caixa de teste no Mercado Pago. O
valor de `MERCADO_PAGO_EXTERNAL_POS_ID` deve ser exatamente o `external_id` do
caixa configurado na aplicação de teste.

## 1. Configurar as credenciais

No painel de desenvolvedores do Mercado Pago, use as credenciais de teste da
aplicação. Não coloque o token no código ou no Git.

No PowerShell, antes de subir o Compose:

```powershell
$env:MERCADO_PAGO_ACCESS_TOKEN = 'APP_USR-seu-token-de-teste'
$env:MERCADO_PAGO_EXTERNAL_POS_ID = 'STORE001POS001'
$env:MERCADO_PAGO_WEBHOOK_URL = 'https://SEU-ENDERECO-PUBLICO/api/payments/webhook'
$env:MERCADO_PAGO_WEBHOOK_SECRET = 'sua-assinatura-do-webhook'
docker compose up --build os-db billing-db rabbitmq os-service billing-service execution-service
```

Para receber webhook localmente, use um túnel HTTPS, como ngrok, apontando para
a porta `8082`. O endpoint público deve terminar em
`/api/payments/webhook`. O Mercado Pago recomenda Webhooks e exige resposta
HTTP 200 ou 201 após o recebimento.

## 2. Criar a OS

```powershell
$order = Invoke-RestMethod -Method Post `
  -Uri http://localhost:8081/api/orders `
  -ContentType 'application/json' `
  -Body (@{
    customerName = 'Cliente Sandbox'
    vehiclePlate = 'ABC1D23'
    description = 'Troca de pastilhas de freio'
  } | ConvertTo-Json)
```

Aguarde o orçamento ser criado pelo RabbitMQ:

```powershell
Start-Sleep -Seconds 3
  $budgets = Invoke-RestMethod http://localhost:8082/api/budgets
  $budget = $budgets | Where-Object { $_.orderId -eq $order.id } | Select-Object -First 1
$budget
```

Defina um valor positivo antes de aprovar o orçamento:

```powershell
$budget = Invoke-RestMethod -Method Patch `
  -Uri "http://localhost:8082/api/budgets/$($budget.id)/amount" `
  -ContentType 'application/json' `
  -Body (@{ amount = 150.00 } | ConvertTo-Json)
```

## 3. Aprovar o orçamento

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:8082/api/budgets/$($budget.id)/approve"
```

## 4A. Testar Checkout Pro

```powershell
$preference = Invoke-RestMethod -Method Post `
  -Uri "http://localhost:8082/api/budgets/$($budget.id)/payment/preference"
$preference.checkoutUrl
Start-Process $preference.checkoutUrl
```

Conclua o pagamento com um usuário comprador de teste. A confirmação deve
chegar ao webhook e gerar `PAYMENT_APPROVED`.

## 4B. Testar QR Code dinâmico

```powershell
$qr = Invoke-RestMethod -Method Post `
  -Uri "http://localhost:8082/api/budgets/$($budget.id)/payment/qr"
$qr | Format-List
$qr.qrData
```

Exiba o valor de `qrData` como QR Code e leia-o pelo aplicativo Mercado Pago
usando uma conta compradora de teste. Depois consulte o orçamento para verificar
`mercadoPagoOrderId`, `mercadoPagoPaymentId` e `status`.

## 5. Validar o fluxo distribuído

```powershell
Start-Sleep -Seconds 5
$updatedBudget = Invoke-RestMethod "http://localhost:8082/api/budgets/$($budget.id)"
$updatedOrder = Invoke-RestMethod "http://localhost:8081/api/orders/$($order.id)"
$execution = Invoke-RestMethod http://localhost:8083/api/execution

$updatedBudget
$updatedOrder
$execution | Where-Object orderId -eq $order.id
```

Resultado esperado após aprovação: o orçamento deve ficar `PAID`, a OS deve
ficar `AGUARDANDO_EXECUCAO` e a execução deve aparecer como `NA_FILA`.

Se o pagamento for rejeitado ou cancelado, o orçamento fica
`PAYMENT_REJECTED` e a OS recebe o evento de compensação
`PAYMENT_REJECTED`.

## 6. Conferir no Swagger

- OS: http://localhost:8081/swagger
- Billing: http://localhost:8082/swagger
- Execution: http://localhost:8083/swagger

O token deve ser informado somente como variável de ambiente ou Secret do
Kubernetes. Para Kubernetes, use `MercadoPago__AccessToken`,
`MercadoPago__ExternalPosId`, `MercadoPago__WebhookUrl` e
`MercadoPago__WebhookSecret` no Deployment do Billing Service.
