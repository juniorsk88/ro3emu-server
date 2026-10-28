# Drop melhorado (DropBoost)

Chave do painel de GM (Ajustes → Drop melhorado) que sobe a chance dos itens que os monstros dropam **raramente**, sem mexer nos demais e **respeitando a taxa de drop do servidor** (Ajustes → Taxas). Código: `src/Ro3Emu.Server/Game/DropBoost.cs` (configuração e curva) e `RewardService.RollBoostedExtras` (o sorteio extra). Testes: `DropBoostTests`. Painel: `admin/dropmath.py`, `tuning_api.py` (`/api/dropboost`), `items_api.py`, `assets/ajustes.js`.

## Arquivo

`<data>/drop-boost.json`, relido uma vez por segundo, sem reiniciar. O painel o grava:

```json
{ "enabled": true, "minPct": 0.01, "thresholdPct": 0.01, "rate": 5, "capPct": 25 }
```

- `enabled`: a chave. Arquivo ausente, inválido ou `false` = chances originais.
- `minPct` (0,001 a 0,5): chance **original** por abate a que o item mais raro chega.
- `thresholdPct` (padrão 0,01, o mesmo do menor drop): itens com chance original igual ou maior **não mudam**. Não pode ser menor que `minPct`.
- `rate` e `capPct`: a taxa de drop e o teto dela (Ajustes → Taxas) que o painel já aplicou às tabelas; o painel os mantém iguais ao aplicado (`dropmath`/`tuning_core.sync_boost_rate`). O servidor só enxerga as chances já multiplicadas (e limitadas pelo teto), então precisa desses dois números para desfazer a conta e achar a chance original. Ausentes = ×1 e sem teto.

## Conta

Tudo sobre a chance **original** `p` do jogo (antes da taxa); a taxa multiplica o resultado:

- `p ≥ T` (T = o maior entre o limite e o menor drop): fica como está. Com taxa 5×: 1% → 5%; com 2×: 0,1% → 0,2%.
- `p < T`: sobe pela curva `T · (p / T)^e`, `e = ln(minPct / T) / ln(1e-7 / T)`, em que 1e-7 (0,00001%) é a menor chance dos dados do jogo e vira `minPct`. Com o limite igual ao menor drop (padrão) `e = 0`: tudo que está abaixo de 0,01% vira 0,01% (piso reto). Com um limite maior (por exemplo 1%) é uma curva proporcional: o mais raro continua o mais raro e o aumento some no limite (0,001% → ~0,06%, 0,01% → ~0,16%, 0,1% → ~0,4%).
- Depois: `min(1, taxa × chance melhorada)`. Nunca fica abaixo do que o servidor já dava.

Exemplo no servidor real (taxa 5×, padrão): um item de 0,03% original (como o Martelo de Smash) segue só a taxa, 0,15%; um de 0,001% sobe a 0,01% e vira 0,05%.

## Teto da taxa de drop (Ajustes → Taxas)

Independente do Drop melhorado e aplicado nas tabelas (`tuning_core.rated_slot`, por "Aplicar e reiniciar"). Com a taxa em 5×, um item de 25% passaria a 100%. Com um teto `C` (%, 0 = sem teto) e taxa > 1: uma chance de slot **já no teto ou acima não muda** (a taxa não mexe); abaixo dele a taxa satura, `C · (1 − (1 − c / C)^taxa)`: perto de `c × taxa` quando `c` é pequena, nunca chega ao teto e mantém a ordem dos itens. Exemplo, taxa 5× e teto 25%: 0,1% → ~0,5%, 5% → ~17%, 10% → ~23%, 20% → ~25%, 25% e acima, como o jogo define. O servidor usa a mesma conta (`DropBoost.Settings.Rated` e sua inversa `Original`) para achar a chance original dos itens raros e aplicar o aumento sobre ela; a taxa (com o teto) vem por cima do aumento.

## Como entra no abate

Só em abate de monstro (`ResolveKill`): as pools sorteadas (`_iDrop` do ambiente do abate e `_iNormalDrop` com condição de mapa) são coletadas e, depois dos sorteios normais, cada item que **quem matou** pode receber tem a chance total do abate calculada (todos os slots e pools juntos, `SlotChance` com `rolls`/`picks`, as mesmas linhas elegíveis de `PickFromGroup`: peso > 0, sem condição de quest/atividade, faixa de nível e lista de classes; linhas do mesmo item somam). Se `Target(p)` está acima do que já é, o item ganha **um** sorteio extra independente com probabilidade `(p' − p) / (1 − p)`, e a soma dos dois cai exatamente em `p'`. A linha que dá o item (quantidade) é escolhida por contribuição. EXP e Zeny (itens 100, 101 e 1) ficam de fora. Prêmios de MVP e de caixas não são tocados. O sorteio extra passa pela atenuação por diferença de nível e pelas taxas do mapa (`map-rates.json`). Os extras ficam em cache por (pools, nível, classe, configuração).

## Painel

As páginas do item (“Quem dropa”) e do monstro mostram a chance **como o servidor dá**: chance original (melhor caso de classe e nível, soma dos slots) × taxa, com o aumento se estiver ligado, com no máximo 3 casas decimais (`<0,001` abaixo de 0,0005%). Ao passar o mouse aparece a chance sem o aumento. O aumento não vale para prêmios de MVP.
