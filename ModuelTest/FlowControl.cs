using MoDuel;
using System;
using System.Collections.Generic;
using System.Linq;
using MoDuel.Data;
using MoDuel.Tools;
using Newtonsoft.Json.Linq;
using MoDuel.Mana;
using System.Threading;
using MoonSharp.Environment;
using System.IO;

namespace ModuelTest {
    class FlowControl {

        /// <summary>
        /// Creates a <see cref="Player"/> from the provided token values.
        /// </summary>
        /// <param name="token"></param>
        /// <param name="env"></param>
        /// <returns></returns>
        public Player ConstructPlayer(JToken token, EnvironmentContainer env) {
            // Get and load the hero.
            var hero = ContentLoader.LoadHero(token["Hero"].ToString(), env.Content, env.Lua);
            // Enumerate and retieve the mana.
            var mana = (token["Mana"] as JArray).ToArray().Select( (m) => new ManaType(m.ToString()));
            // Create the hero and return also getting the user id from the token.
            return new Player(token["ID"].ToString(), hero, new ManaPool(mana.ToArray()));
        }

        /// <summary>
        /// Adds the cards fround in token to the provided player.
        /// </summary>
        public void AddCardsToPlayer(DuelFlow flow, Player player, JToken token, EnvironmentContainer env) {
            // Go through each card found in the json token.
            foreach(string cardId in token["Cards"]) {
                // Load the card.
                var card = ContentLoader.LoadCard(cardId, env.Content, env.Lua);
                // Create a new instance for each card.
                var CI = flow.CreateCardInstance(card, player);
                // Ensure the card has the correct cost.
                CI.Cost = CI.ImprintCost;
                // Add the card to the players hand.
                player.AddCardToHand(CI);
            }
        }

        /// <summary>
        /// Creates and starts the test duel.
        /// </summary>
        public void Start() {

            // Open the settings file.
            var settings = JObject.Parse(File.ReadAllText("game.json"));

            // Create the random object. If the settings file has a provided seed we use it.
            ManagedRandom rand;
            if (settings["RandomSeed"].Type == JTokenType.Null)
                rand = new ManagedRandom();
            else
                rand = new ManagedRandom(settings["RandomSeed"].ToObject<int>());

            EnvironmentContainer env = new EnvironmentContainer() {
                AnimationBlocker = new PlaybackBlockingHandler(),
                Content = new LoadedContent(),
                Lua = new LuaEnvironment(),
                Random = rand,
                Settings = new DuelSettings()
            };

            // Provide the content loader with directory information.
            ContentLoader.SetContentDirectory(settings["ContentDirectory"].ToString());
            // Register loading to the current environemt.
            ContentLoader.RegisterLoads(env.Content, env.Lua);
            // Tell the loader to log anything that is loaded.
            ContentLoader.LogLoads = true;

            // Get the commands stored in the json file as we will have to load them.
            var commands = (settings["LoadCommands"] as JArray).Values<string>();

            // Apart from commands we also have to manually load the lua functions we provide to the DuelFlow.
            var cmds = new List<string>(commands) {
                settings["GameStart"].ToString(),
                settings["ChangeTurn"].ToString(),
                settings["GameEnd"].ToString(),
            };

            // Load all the system and command actions.
            foreach (var cmd in cmds)
                ContentLoader.LoadAction(cmd, env.Content, env.Lua);

            // Tell the duel flow what we want it to do for the varaible actions.
            env.Settings.GameStartAction = env.Content.GetAction(settings["GameStart"].ToString());
            env.Settings.ChangeTurnAction = env.Content.GetAction(settings["ChangeTurn"].ToString());
            env.Settings.GameEndAction = env.Content.GetAction(settings["GameEnd"].ToString());
            // Disable animations as we won't see anything on console application and it will just cause slow down.
            env.Settings.AnimationSpeed = DuelSettings.NO_ANIM;

            // Create the players.
            var Player1 = ConstructPlayer(settings["Player1"], env);
            var Player2 = ConstructPlayer(settings["Player2"], env);

            // Write a summary of every thing we have loaded.
            Console.WriteLine("------------------------------");
            foreach (string s in env.Content.GetAllKeysSummary()) {
                Console.WriteLine(s);
            }

            // Determine who goes fist. Set as player1 to initalise and causes less lines of code.
            Player goesFist = Player1;
            switch (settings["GoesFirst"].ToString()) {
                case "Player2":
                    goesFist = Player2;
                    break;
                case "Random":
                    // If the first player is random we need to use the ManagedRandom to decide.
                    int val = env.Random.Next(0, 2);
                    if (val == 0)
                        goesFist = Player2;
                    break;
            }

            // Create and ready the duel flow.
            DuelFlow flow = new DuelFlow(env, Player1, Player2, goesFist);

            // Add all the cards the players asked for to their hands.
            AddCardsToPlayer(flow, Player1, settings["Player1"], env);
            AddCardsToPlayer(flow, Player2, settings["Player2"], env);

            // Provide the callback functions with a simple console print statement.
            flow.OutBoundDelegate += (object sender, ClientRequest data) => { Console.WriteLine(data.RequestId + " sent to all players."); };
            Player1.OutBoundDelegate += (object sender, ClientRequest data) => { Console.WriteLine(data.RequestId + " sent to " + Player1.UserId); };
            Player2.OutBoundDelegate += (object sender, ClientRequest data) => { Console.WriteLine(data.RequestId + " sent to " + Player2.UserId); };

            // Stop any loading from happening during gameplay as it will cause slowdown.
            ContentLoader.DeregisterLoads(env.Lua);

            // Start the duel.
            Thread loop = flow.Start();

            while (flow.State.OnGoing) {
                //Delay the thread incase of ongoing logic.
                Thread.Sleep(50);

                // Get the player whose turn it currently is.
                var towner = flow.State.CurrentTurn.Owner;
                
                // Display information about the game.
                Console.WriteLine();
                // Whose turn it is.
                Display.Turn(flow.State.CurrentTurn);
                // TurnOwner's stats.
                Display.PlayerStats(towner);
                // Other Player's stats.
                Display.PlayerStats(flow.State.GetOpposingPlayer(towner));
                Console.WriteLine();
                // And the field.
                Display.Field(flow.State.Field);
                Console.WriteLine();
                Console.WriteLine();

                // Get input from the console.
                string x = Console.ReadLine();
                // Clear the consloe for the next command.
                Console.Clear();
                // Split the command into its components.
                string[] splits = x.Split(' ');

                switch (splits[0].ToLower()) {
                    case "level":
                        flow.EnqueueCommand("CMDLevelUp", towner);
                        break;
                    case "levelup":
                        flow.EnqueueCommand("CMDLevelUp", towner);
                        break;
                    case "disc":
                        if (splits.Length != 2) {
                            Console.WriteLine("Please provide a card number. example: disc 2");
                            break;
                        }
                        if (towner.Hand.Count == 0) {
                            Console.WriteLine("Your hand is empty.");
                            break;
                        }
                        if (int.TryParse(splits[1], out int index)) {
                            if (index > towner.Hand.Count - 1) {
                                Console.WriteLine("Card number provided is to high.");
                                break;
                            }
                            if (index < 0) {
                                Console.WriteLine("Card number cannot be negative");
                                break;
                            }
                            flow.EnqueueCommand("CMDDiscard", towner, towner.Hand.ElementAt(index));
                            break;
                        }
                        else {
                            Console.WriteLine("Please provide a card number. example: disc 2");
                            break;
                        }
                    case "discard":
                        if (splits.Length != 2) {
                            Console.WriteLine("Please provide a card number. example: discard 2");
                            break;
                        }
                        if (towner.Hand.Count == 0) {
                            Console.WriteLine("Your hand is empty.");
                            break;
                        }
                        if (int.TryParse(splits[1], out int index2)) {
                            if (index2 > towner.Hand.Count - 1) {
                                Console.WriteLine("Card number provided is to high.");
                                break;
                            }
                            if (index2 < 0) {
                                Console.WriteLine("Card number cannot be negative");
                                break;
                            }
                            flow.EnqueueCommand("CMDDiscard", towner, towner.Hand.ElementAt(index2));
                            break;
                        }
                        else {
                            Console.WriteLine("Please provide a card number. example: discard 2");
                            break;
                        }
                    case "hand":
                        Display.Collection("Hand", towner.Hand);
                        break;
                    case "grave":
                        Display.Collection("Grave", towner.Grave);
                        break;
                    case "revive":
                        flow.EnqueueCommand("CMDRevive", towner);
                        break;
                    case "rev":
                        flow.EnqueueCommand("CMDRevive", towner);
                        break;
                    case "play":
                        if (splits.Length != 3) {
                            Console.WriteLine("Please provide a card number and a position. example: play 2 4");
                            Console.WriteLine("This would place card 2 in position 4.");
                            break;
                        }
                        if (towner.Hand.Count == 0) {
                            Console.WriteLine("Your hand is empty.");
                            break;
                        }
                        if (int.TryParse(splits[1], out int index3)) {
                            if (index3 > towner.Hand.Count - 1) {
                                Console.WriteLine("Card number provided is to high.");
                                break;
                            }
                            if (index3 < 0) {
                                Console.WriteLine("Card number cannot be negative");
                                break;
                            }
                            if (int.TryParse(splits[2], out int pos)) {
                                if (pos > flow.State.Field.Count()-1 || pos < 0) {
                                    Console.WriteLine("Position must be a spot on the field. MaxPos: " + flow.State.Field.Count());
                                    break;
                                }
                                flow.EnqueueCommand("CMDPlayCard", towner, towner.Hand.ElementAt(index3), flow.State.Field[pos]);
                                break;
                            }
                            else {
                                Console.WriteLine("Please provide a card number and a position. example: play 2 4");
                                Console.WriteLine("This would place card 2 in position 4.");
                                break;
                            }
                        }
                        else {
                            Console.WriteLine("Please provide a card number and a position. example: play 2 4");
                            Console.WriteLine("This would place card 2 in position 4.");
                            break;
                        }
                    case "charge":
                        flow.EnqueueCommand("CMDCharge", towner);
                        break;
                    case "end":
                        loop.Abort();
                        break;
                    case "help":
                        Console.WriteLine("Commands: [charge, play, level(up), disc(ard), rev(ive), hand, grave, help]");
                        break;
                    default:
                        Console.WriteLine(x + " is not a valid command.");
                        Console.WriteLine("Commands: [charge, play, level(up), disc(ard), rev(ive), hand, grave, help]");
                        break;
                }
            }


            Console.WriteLine("Game Over - Press Any Key to Terminate");
            Console.ReadLine();

        }


    }
}
