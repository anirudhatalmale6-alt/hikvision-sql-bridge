# SIBHIK — Hikvision → SQL Server

Serviço configurável para Windows (nome do serviço: **SIBHIK**) que recebe, em
tempo real, as picagens dos terminais de controlo de acessos Hikvision e
grava-as numa base de dados SQL Server (por omissão `Assiduidadev3` → tabela
`TG_MOVIMENTOS`).

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
- **Respeita o trigger existente** — `ID_NUMERO` é gravado a 0; o trigger
  `[dbo].[Movimentos_INSERT]` preenche-o automaticamente a seguir ao INSERT,
  cruzando `(ID_TIPO_IDENTIFICADOR, ID_IDENTIFICADOR)` com `TA_IDENTIFICADORES`.

## Mapeamento para TG_MOVIMENTOS

| Coluna                 | Valor                                                            |
|------------------------|-----------------------------------------------------------------|
| ID                     | identity (automático)                                           |
| ID_NUMERO              | 0 no INSERT — preenchido pelo trigger a partir de TA_IDENTIFICADORES |
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
2. Preencher a ligação ao SQL Server e a lista de equipamentos. Há duas formas
   de indicar o SQL Server:
   - por campos (`Server`, `Database`, `User`/`Password` ou `UseWindowsAuth`); ou
   - colando uma connection string completa em `ConnectionString` (se preenchida,
     é usada tal e qual — ex.: a mesma que o bevotech usa,
     `Data Source=DESKTOP-S8CKGL7\SQL;Initial Catalog=SPBA1;User Id=sa;Password=...;MultipleActiveResultSets=True`).
3. Na versão final, uma janela de configuração faz isto graficamente (ao estilo
   da janela de "Propriedades de ligação de dados" do Windows), com botão de
   "Testar ligação".

## Compilar e testar

```bash
dotnet build
dotnet test
```

## Como testar sem terminal (só a parte de SQL)

Para validar a ligação e o trigger na máquina onde vai correr, sem precisar de
um terminal Hikvision:

```powershell
# 1) Testar apenas a ligação ao SQL Server
SIBHIK.exe --test-connection

# 2) Inserir uma picagem de teste em TG_MOVIMENTOS
#    (use um nº de utilizador que já exista na TA_IDENTIFICADORES)
SIBHIK.exe --simulate 489 face
SIBHIK.exe --simulate 489 card
```

O `--simulate` grava a linha com ID_NUMERO = 0 e o método escolhido; depois
confirma-se na tabela que o trigger `Movimentos_INSERT` preencheu o ID_NUMERO a
partir da TA_IDENTIFICADORES. Assim testa-se toda a parte de SQL antes de ligar
os equipamentos.

## Instalar como Serviço do Windows

```powershell
# publicar
dotnet publish src/HikvisionSqlBridge.Service -c Release -r win-x64 --self-contained false -o C:\SIBHIK

# criar e arrancar o serviço
sc.exe create SIBHIK binPath= "C:\SIBHIK\HikvisionSqlBridge.Service.exe" start= auto
sc.exe start SIBHIK
```

## Nota sobre a ligação ao SQL (Encrypt)

Por omissão a ligação usa `Encrypt = false` (e `TrustServerCertificate = true`),
tal como o software bevotech. O driver moderno (Microsoft.Data.SqlClient) exige
encriptação por omissão, o que faz falhar o login contra instâncias locais/
internas sem certificado TLS válido — daí desligarmos a encriptação por defeito.
Se o servidor tiver certificado válido e quiser encriptar, ponha `"Encrypt": true`.

> Estado: núcleo funcional (ISAPI + parsing + gravação em SQL + reconexão +
> logs) com testes a passar. Falta afinar, com o equipamento real, o
> mapeamento exacto do método de verificação e dos códigos de acesso
> concedido/negado, e adicionar a janela gráfica de configuração.
