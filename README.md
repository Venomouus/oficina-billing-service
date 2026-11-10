# Oficina Billing Service

Microsserviço responsável por orçamentos e pelo fluxo inicial de pagamento.

## Stack

- ASP.NET Core 8
- MongoDB exclusivo do serviço
- RabbitMQ para integração assíncrona
- Swagger/OpenAPI

## Eventos

- Consome `OS_CREATED` e gera um orçamento.
- Publica `BUDGET_CREATED`.
- Publica `BUDGET_APPROVED` após aprovação.
- Publica `PAYMENT_APPROVED` após confirmação do pagamento.

O pagamento atual utiliza um fluxo de demonstração. A integração com o Mercado Pago será adicionada mantendo estes eventos.

## Executar

```powershell
dotnet run
```

Configuração padrão: MongoDB em `localhost:27017` e RabbitMQ em `localhost:5672`.
