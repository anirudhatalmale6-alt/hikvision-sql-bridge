# Hikvision → SQL Server Bridge

Serviço configurável para Windows que recebe, em tempo real, as picagens dos
terminais de controlo de acessos Hikvision e grava-as numa base de dados SQL
Server (por omissão `Assiduidadev3` → tabela `TG_MOVIMENTOS`).

O iVMS-4200 continua a ser usado normalmente para gerir utilizadores e inscrever
impressão digital / Face ID. Este serviço corre em paralelo, sem interferir com
o iVMS: liga-se directamente ao terminal pelo canal de eventos **ISAPI**
(long-polling), que suporta várias ligações em simultâneo.

## Características

- **Configurável** — nada fixo no código. A mesma ferramenta serve qualquer
  servidor Windows e qualquer instância SQL Server; muda-se apenas o
  `config.json`.
- **Vários terminais** — lista de equipamentos (IP / porta / utilizador /
  palavra-passe), de modelos diferentes.
- **Multi-LAN** — cada terminal pode estar numa gama de rede diferente, desde
  que exista rota entre o servidor e o equipamento.
- **Reconexão automática** — se o link cair, volta a ligar sozinho com backoff
  progressivo.
- **Logs para auditoria** — ficheiros rotativos diários, com retenção
  configurável, e registo opcional dos eventos brutos para diagnóstico.
- **Chave primária segura** — grava o número do utilizador em `ID_NUMERO` para
  garantir a unicidade da chave (`ID_NUMERO + ID_DATAHORA + ID_MAIN_CODE`) e
  nunca perder picagens no mesmo segundo.

## Mapeamento para TG_MOVIMENTOS

| Coluna                 | Valor                                                            |
|------------------------|-----------------------------------------------------------------|
| ID                     | identity (automático)                                           |
| ID_NUMERO              | número do utilizador (para chave única)                         |
| ID_DATAHORA            | data/hora da picagem                                            |
| ID_MAIN_CODE           | 0                                                               |
| ID_TIPO_IDENTIFICADOR  | 1=RFID · 2=Impressão digital/Face · 3=PIN · 5=Matrícula · 6=NFC · 7=QR |
| ID_IDENTIFICADOR       | nº do utilizador com 5 dígitos ("00489")                        |
| ID_TIPO                | "I" (Indiferenciado)                                            |
| ID_SUPPORT_CODE        | 0                                                               |
| ID_IPTERMINAL          | IP do equipamento                                              |
| ID_END                 | 0                                                               |
| ID_LATITUDE/LONGITUDE  | NULL                                                            |
| ID_UTILIZADOR          | NULL                                                            |
| ID_DATA_SISTEMA        | automático (default getdate())                                 |
| ID_COD_CLASSFICACAO    | 0                                                               |

Só são gravadas as picagens válidas (acesso concedido).

## Estrutura do projecto

```
src/HikvisionSqlBridge.Core      Lógica (ISAPI, parsing, mapeamento, SQL)
src/HikvisionSqlBridge.Service   Serviço do Windows (host)
tests/HikvisionSqlBridge.Tests   Testes unitários
```

## Configuração

1. Copiar `config.sample.json` para `config.json` (fica ao lado do executável).
2. Preencher a ligação ao SQL Server e a lista de equipamentos.
3. Na versão final, uma janela de configuração faz isto graficamente (ao estilo
   da janela de "Propriedades de ligação de dados" do Windows), com botão de
   "Testar ligação".

## Compilar e testar

```bash
dotnet build
dotnet test
```

## Instalar como Serviço do Windows

```powershell
# publicar
dotnet publish src/HikvisionSqlBridge.Service -c Release -r win-x64 --self-contained false -o C:\HikvisionSqlBridge

# criar e arrancar o serviço
sc.exe create HikvisionSqlBridge binPath= "C:\HikvisionSqlBridge\HikvisionSqlBridge.Service.exe" start= auto
sc.exe start HikvisionSqlBridge
```

> Estado: núcleo funcional (ISAPI + parsing + gravação em SQL + reconexão +
> logs) com testes a passar. Falta afinar, com o equipamento real, o
> mapeamento exacto do método de verificação e dos códigos de acesso
> concedido/negado, e adicionar a janela gráfica de configuração.
