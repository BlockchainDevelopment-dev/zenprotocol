Feature: Block Versioning Tests

  Scenario: Should accept a v0 block with non-v1-satisfying stats
    Given chain params
      | Key                      | Value |
      | nextInhibitionPercentage | 50    |

    Given genesisTx locks 100 Zen to key1
    And genesis has genesisTx

    When validating an empty block bk1 extending tip
    Then tip should be bk1

    When validating an empty v0 block bk2 extending tip
    Then tip should be bk2

  Scenario: Should accept an empty v1 block with non-v1-satisfying stats
    Given chain params
      | Key                      | Value |
      | nextInhibitionPercentage | 50    |

    Given genesisTx locks 100 Zen to key1
    And genesis has genesisTx

    When validating an empty block bk1 extending tip
    Then tip should be bk1

    When validating an empty v1 block bk2 extending tip
    Then tip should be bk2

  Scenario: Should accept a v1 block containing v0 txs with non-v1-satisfying stats
    Given chain params
      | Key                      | Value |
      | nextInhibitionPercentage | 50    |

    Given genesisTx locks 100 Zen to key1
    And genesis has genesisTx

    When validating an empty block bk1 extending tip
    Then tip should be bk1

    Given tx has the input genesisTx index 0
    And tx locks 100 Zen to key2
    And tx is signed with key1
    
    When validating block bk2 containing tx extending tip
    Then tip should be bk2

  Scenario: Should reject a block containing v1 txs with non-v1-satisfying stats
    Given chain params
      | Key                      | Value |
      | nextInhibitionPercentage | 50    |

    Given genesisTx locks 100 Zen to key1
    And genesis has genesisTx

    When validating an empty block bk1 extending tip
    Then tip should be bk1

    Given voteTx has the input genesisTx index 0
    And voteTx votes on allocation of 10 with 100 Zen in interval 0
    And voteTx is signed with key1

    When validating block bk2 containing voteTx extending tip
    Then tip should be bk1
    
  Scenario: Should accept a block containing v1 txs with v1-satisfying stats
    Given chain params
      | Key                      | Value |
      | nextInhibitionPercentage | 50    |
    
    And genesisTx locks 100 Zen to key1
    And genesis has genesisTx 

    When validating an empty v0 block extending tip 
      # V0 => stats: 0%
    
    Given voteTx has the input genesisTx index 0
    And voteTx votes on allocation of 10 with 100 Zen in interval 0
    And voteTx is signed with key1

    When validating a v1 block bk2 containing voteTx extending tip
      # V1 => stats: 50%
    Then tip should be bk2


  Scenario: Should reject a block containing v1 txs with non-v1-satisfying stats (2)
    Given chain params
      | Key                      | Value |
      | nextInhibitionPercentage | 55    |

    And genesisTx locks 100 Zen to key1
    And genesis has genesisTx

    When validating an empty v0 block bk1 extending tip 
      # V0 => stats: 0%

    Given voteTx has the input genesisTx index 0
    And voteTx votes on allocation of 10 with 100 Zen in interval 0
    And voteTx is signed with key1

    When validating a v1 block bk2 containing voteTx extending tip
      # V1 => stats: 50%
    Then tip should be bk1