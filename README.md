# Oficina Billing Service

Microsservico responsavel por orcamentos e pelo fluxo inicial de pagamento.

## Stack

- ASP.NET Core 8
- MongoDB exclusivo do servico
- RabbitMQ para integracao assincrona
- Swagger/OpenAPI

## Eventos

- Consome `OS_CREATED` e gera um orcamento.
- Publica `BUDGET_CREATED`.
- Publica `BUDGET_APPROVED` apos aprovacao.
- Publica `BUDGET_REJECTED` quando o cliente recusa.
- Publica `PAYMENT_APPROVED` apos confirmacao do pagamento.

O endpoint `/payment` simula a confirmacao local. O Checkout Pro sandbox usa
`/payment/preference` e confirma via `/api/payments/webhook`. Um webhook
repetido nao duplica o evento; uma recusa tardia nao desfaz um pagamento `PAID`.
Se uma tentativa rejeitada for posteriormente aprovada pelo provedor, o fluxo
pode prosseguir.

Eventos pendentes ficam no proprio documento do orcamento (outbox), gravados
atomicamente com o status. Um worker reenvia a cada 2 segundos com mensagens
persistentes e confirmacao do RabbitMQ. Assim, indisponibilidade temporaria do
broker nao perde a confirmacao de pagamento. Eventos duplicados podem ser
entregues; o OS Service trata-os de forma idempotente.
O consumidor de `OS_CREATED` reconecta ao RabbitMQ e usa indice unico em
`OrderId` para impedir orcamentos duplicados mesmo com entregas concorrentes.
Falhas temporarias aguardam 30 segundos em `billing-service-events.retry`;
apos 5 novas tentativas, ou se a mensagem for malformada, vao para
`billing-service-events.dead`. Investigue e reenvie manualmente apos corrigir
a causa. Se ja houver `OrderId` duplicado no MongoDB, remova a duplicidade
com cuidado antes de iniciar o consumidor; nenhuma limpeza e automatica.
Falha de execucao nao dispara estorno automatico: a OS registra
`EXECUCAO_COM_ERRO` e um estorno de pagamento capturado requer decisao humana.

## Endpoints principais

- `GET /api/budgets`
- `GET /api/budgets/{id}`
- `PATCH /api/budgets/{id}/amount`
- `POST /api/budgets/{id}/approve`
- `POST /api/budgets/{id}/reject`
- `POST /api/budgets/{id}/payment`
- `POST /api/budgets/{id}/payment/preference` cria um checkout sandbox do Mercado Pago.
- `POST /api/budgets/{id}/payment/qr` cria uma order QR dinâmico sandbox e retorna `qrData`.
- `POST /api/payments/webhook` recebe notificações `payment` e `order`.

## Executar

```powershell
dotnet run
```

Configuracao padrao: MongoDB em `localhost:27017` e RabbitMQ em `localhost:5672`.

Para testar o Mercado Pago, consulte o guia [TESTING-MERCADO-PAGO.md](TESTING-MERCADO-PAGO.md).

## Testes e cobertura

Com .NET 8 SDK e Docker disponiveis, execute `powershell -File Tests/BillingService.Tests/run-coverage.ps1` na raiz deste servico. O script roda os testes em Release, grava um relatorio Cobertura em `Tests/BillingService.Tests/TestResults/coverage-*/` (fora de `bin/` e `obj/`) e falha se a cobertura de linhas da assembly de producao `BillingService` for inferior a 80%. Nenhuma classe da assembly e excluida da medicao.

Os testes de endpoints usam MongoDB temporario em container e um banco exclusivo por caso. Os testes de mensageria usam RabbitMQ temporario em container. Chamadas ao Mercado Pago usam apenas um handler HTTP falso; nao exigem credenciais ou acesso externo ao provedor.
O relatorio Cobertura tambem fica disponivel no artefato `billing-service-coverage` da execucao do workflow de CI.
O [painel SonarCloud do PR](https://sonarcloud.io/dashboard?id=Venomouus_oficina-billing-service&pullRequest=1)
mostra o quality gate e a cobertura de codigo novo; o script local/CI exige
separadamente pelo menos 80% de linhas da assembly completa.
