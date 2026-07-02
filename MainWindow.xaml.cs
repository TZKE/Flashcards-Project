<Window x:Class="AIFlashcardMaker.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="AI Flashcard Maker"
        Width="1460"
        Height="920"
        MinWidth="1180"
        MinHeight="760"
        WindowStartupLocation="CenterScreen">

    <Grid Background="{StaticResource AppBgBrush}">

        <!-- AUTH SCREEN -->
        <Grid x:Name="AuthGrid" Margin="34">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="0.46*"/>
                <ColumnDefinition Width="0.54*"/>
            </Grid.ColumnDefinitions>

            <StackPanel Grid.Column="0" Margin="28" VerticalAlignment="Center">

                <StackPanel Orientation="Horizontal" Margin="0,0,0,28">
                    <Border Width="58" Height="58" Background="{StaticResource PrimaryBrush}" CornerRadius="16" Margin="0,0,14,0">
                        <TextBlock Text="⚡" FontSize="32" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="White"/>
                    </Border>

                    <StackPanel VerticalAlignment="Center">
                        <TextBlock Text="AI Flashcard Maker" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                        <TextBlock Text="Premium medical study workspace" FontSize="13" Foreground="{StaticResource MutedBrush}"/>
                    </StackPanel>
                </StackPanel>

                <TextBlock Text="Generate, organize, and study high-yield flashcards."
                           FontSize="42"
                           FontWeight="Bold"
                           Foreground="{StaticResource TextBrush}"
                           TextWrapping="Wrap"
                           Margin="0,0,30,16"/>

                <TextBlock Text="A clean Windows desktop app for medical students: decks, Z.ai generation, spaced repetition, export, and animated study feedback."
                           FontSize="16"
                           Foreground="{StaticResource MutedBrush}"
                           TextWrapping="Wrap"
                           Margin="0,0,46,26"/>

                <Border Style="{StaticResource CardBorder}" Margin="0,0,46,22">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="88"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>

                        <Image Source="/Assets/Animations/Study.gif"
                               Width="70"
                               Height="70"
                               Stretch="Uniform"
                               VerticalAlignment="Center"/>

                        <StackPanel Grid.Column="1" VerticalAlignment="Center">
                            <TextBlock Text="Study smarter, not harder" FontSize="20" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Create decks, review weak cards, and export to Anki when needed."
                                       TextWrapping="Wrap"
                                       Foreground="{StaticResource MutedBrush}"
                                       Margin="0,8,0,0"/>
                        </StackPanel>
                    </Grid>
                </Border>

                <UniformGrid Columns="2" Margin="0,0,46,0">
                    <Border Style="{StaticResource SoftCardBorder}" Margin="0,0,10,10">
                        <StackPanel>
                            <TextBlock Text="🗂️" FontSize="26"/>
                            <TextBlock Text="Multi-deck" FontWeight="Bold" Foreground="{StaticResource TextBrush}" Margin="0,8,0,2"/>
                            <TextBlock Text="Organize topics cleanly." Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Border>

                    <Border Style="{StaticResource SoftCardBorder}" Margin="0,0,0,10">
                        <StackPanel>
                            <TextBlock Text="🤖" FontSize="26"/>
                            <TextBlock Text="Z.ai Cards" FontWeight="Bold" Foreground="{StaticResource TextBrush}" Margin="0,8,0,2"/>
                            <TextBlock Text="Generate from notes." Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Border>

                    <Border Style="{StaticResource SoftCardBorder}" Margin="0,0,10,0">
                        <StackPanel>
                            <TextBlock Text="🔥" FontSize="26"/>
                            <TextBlock Text="Streaks" FontWeight="Bold" Foreground="{StaticResource TextBrush}" Margin="0,8,0,2"/>
                            <TextBlock Text="Track daily progress." Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Border>

                    <Border Style="{StaticResource SoftCardBorder}">
                        <StackPanel>
                            <TextBlock Text="📤" FontSize="26"/>
                            <TextBlock Text="Export" FontWeight="Bold" Foreground="{StaticResource TextBrush}" Margin="0,8,0,2"/>
                            <TextBlock Text="Copy or save for Anki." Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Border>
                </UniformGrid>

            </StackPanel>

            <Border Grid.Column="1"
                    Style="{StaticResource CardBorder}"
                    Width="560"
                    HorizontalAlignment="Center"
                    VerticalAlignment="Center"
                    Padding="34">

                <TabControl>
                    <TabItem Header="Login">
                        <StackPanel Margin="10">

                            <StackPanel HorizontalAlignment="Center" Margin="0,8,0,26">
                                <Border Width="54" Height="54" Background="{StaticResource PrimarySoftBrush}" CornerRadius="16" HorizontalAlignment="Center">
                                    <TextBlock Text="⚡" FontSize="30" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                </Border>

                                <TextBlock Text="Welcome back 👋"
                                           FontSize="28"
                                           FontWeight="Bold"
                                           Foreground="{StaticResource TextBrush}"
                                           HorizontalAlignment="Center"
                                           Margin="0,14,0,4"/>

                                <TextBlock Text="Sign in to continue your flashcard workspace."
                                           FontSize="14"
                                           Foreground="{StaticResource MutedBrush}"
                                           HorizontalAlignment="Center"/>
                            </StackPanel>

                            <TextBlock Text="Email" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                            <TextBox x:Name="LoginEmailBox" Height="44" Margin="0,8,0,16"/>

                            <TextBlock Text="Password" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                            <PasswordBox x:Name="LoginPasswordBox" Height="44" Margin="0,8,0,22"/>

                            <Button Content="Login"
                                    Style="{StaticResource PremiumButton}"
                                    Click="Login_Click"
                                    Height="46"/>

                            <Border Background="{StaticResource WarningSoftBrush}" CornerRadius="10" Padding="14" Margin="0,24,0,0">
                                <TextBlock Text="New users should create an account using a demo activation code."
                                           Foreground="{StaticResource WarningBrush}"
                                           TextWrapping="Wrap"/>
                            </Border>
                        </StackPanel>
                    </TabItem>

                    <TabItem Header="Create Account">
                        <StackPanel Margin="10">

                            <StackPanel HorizontalAlignment="Center" Margin="0,8,0,24">
                                <Image Source="/Assets/Animations/Success.gif"
                                       Width="72"
                                       Height="72"
                                       Stretch="Uniform"
                                       HorizontalAlignment="Center"/>

                                <TextBlock Text="Create account"
                                           FontSize="28"
                                           FontWeight="Bold"
                                           Foreground="{StaticResource TextBrush}"
                                           HorizontalAlignment="Center"
                                           Margin="0,10,0,4"/>

                                <TextBlock Text="Use a local demo code to unlock the app."
                                           FontSize="14"
                                           Foreground="{StaticResource MutedBrush}"
                                           HorizontalAlignment="Center"/>
                            </StackPanel>

                            <TextBlock Text="Email" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                            <TextBox x:Name="SignupEmailBox" Height="44" Margin="0,8,0,14"/>

                            <TextBlock Text="Password" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                            <PasswordBox x:Name="SignupPasswordBox" Height="44" Margin="0,8,0,14"/>

                            <TextBlock Text="Activation Code" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                            <TextBox x:Name="SignupCodeBox" Height="44" Margin="0,8,0,20"/>

                            <Button Content="Create Account"
                                    Style="{StaticResource PremiumButton}"
                                    Background="{StaticResource SuccessBrush}"
                                    Click="Signup_Click"
                                    Height="46"/>

                            <Border Background="{StaticResource PrimarySoftBrush}" CornerRadius="10" Padding="14" Margin="0,22,0,0">
                                <TextBlock Foreground="{StaticResource PrimaryBrush}" FontFamily="Consolas" FontSize="12"
                                           Text="Demo codes: FLASH-MONTH-2026, FLASH-YEAR-2026, FLASH-LIFE-2026"/>
                            </Border>
                        </StackPanel>
                    </TabItem>
                </TabControl>
            </Border>
        </Grid>

        <!-- MAIN APP -->
        <Grid x:Name="AppGrid" Visibility="Collapsed">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="260"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- SIDEBAR -->
            <Border Grid.Column="0" Background="{StaticResource SidebarBrush}" Padding="22,24">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="82"/>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="130"/>
                    </Grid.RowDefinitions>

                    <StackPanel Orientation="Horizontal">
                        <Border Width="48" Height="48" Background="{StaticResource PrimaryBrush}" CornerRadius="14" Margin="0,0,12,0">
                            <TextBlock Text="⚡" FontSize="27" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="White"/>
                        </Border>

                        <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="AI Flashcards" FontSize="20" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="V7.4 Sneat UI" FontSize="12" Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>
                    </StackPanel>

                    <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
                        <StackPanel>
                            <TextBlock Text="WORKSPACE" Foreground="{StaticResource SoftMutedBrush}" FontSize="11" FontWeight="Bold" Margin="0,8,0,10"/>

                            <Button Content="🏠  Dashboard" Style="{StaticResource NavButton}" Click="Dashboard_Click"/>
                            <Button Content="✨  Generate" Style="{StaticResource NavButton}" Click="GeneratePage_Click"/>
                            <Button Content="📥  Import JSON" Style="{StaticResource NavButton}" Click="ImportPage_Click"/>
                            <Button Content="🗂️  My Decks" Style="{StaticResource NavButton}" Click="DecksPage_Click"/>
                            <Button Content="🎯  Study Mode" Style="{StaticResource NavButton}" Click="StudyPage_Click"/>
                            <Button Content="✏️  Preview / Edit" Style="{StaticResource NavButton}" Click="PreviewPage_Click"/>
                            <Button Content="📤  Export" Style="{StaticResource NavButton}" Click="ExportPage_Click"/>

                            <TextBlock Text="SYSTEM" Foreground="{StaticResource SoftMutedBrush}" FontSize="11" FontWeight="Bold" Margin="0,22,0,10"/>

                            <Button Content="🔑  AI Settings" Style="{StaticResource NavButton}" Click="SettingsPage_Click"/>
                            <Button Content="👤  Account" Style="{StaticResource NavButton}" Click="AccountPage_Click"/>
                        </StackPanel>
                    </ScrollViewer>

                    <Border Grid.Row="2" Background="{StaticResource PrimarySoftBrush}" CornerRadius="14" Padding="16">
                        <StackPanel>
                            <Image Source="/Assets/Animations/streakfire.gif"
                                   Width="42"
                                   Height="42"
                                   HorizontalAlignment="Left"
                                   Stretch="Uniform"/>
                            <TextBlock Text="Keep your streak" Foreground="{StaticResource TextBrush}" FontWeight="Bold" Margin="0,8,0,4"/>
                            <TextBlock Text="Review due cards daily for long-term retention."
                                       Foreground="{StaticResource MutedBrush}"
                                       FontSize="12"
                                       TextWrapping="Wrap"/>
                        </StackPanel>
                    </Border>
                </Grid>
            </Border>

            <!-- RIGHT APP AREA -->
            <Grid Grid.Column="1">
                <Grid.RowDefinitions>
                    <RowDefinition Height="82"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="30"/>
                </Grid.RowDefinitions>

                <!-- TOP BAR -->
                <Border Grid.Row="0" Background="{StaticResource AppBgBrush}" Padding="24,16">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="650"/>
                            <ColumnDefinition Width="112"/>
                        </Grid.ColumnDefinitions>

                        <Border Background="{StaticResource CardBrush}" CornerRadius="12" Padding="16,10" Width="420" HorizontalAlignment="Left">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="🔎" FontSize="18" Margin="0,0,12,0"/>
                                <TextBlock Text="Search decks, cards, tags..."
                                           Foreground="{StaticResource SoftMutedBrush}"
                                           VerticalAlignment="Center"/>
                            </StackPanel>
                        </Border>

                        <TextBlock x:Name="UserSummaryText"
                                   Grid.Column="1"
                                   Foreground="{StaticResource MutedBrush}"
                                   HorizontalAlignment="Right"
                                   VerticalAlignment="Center"
                                   FontSize="13"/>

                        <Button Grid.Column="2"
                                Content="Logout"
                                Style="{StaticResource SoftButton}"
                                Click="Logout_Click"
                                Margin="14,0,0,0"/>
                    </Grid>
                </Border>

                <!-- CONTENT -->
                <Grid Grid.Row="1" Margin="24,10,24,14" x:Name="ContentRoot">

                    <!-- DASHBOARD -->
                    <Grid x:Name="PageDashboard">
                        <ScrollViewer VerticalScrollBarVisibility="Auto">
                            <StackPanel>

                                <Grid Margin="0,0,0,18">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="330"/>
                                    </Grid.ColumnDefinitions>

                                    <Border Style="{StaticResource CardBorder}" Padding="28" Margin="0,0,18,0">
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="180"/>
                                            </Grid.ColumnDefinitions>

                                            <StackPanel>
                                                <TextBlock Text="Welcome back 👋" FontSize="28" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                                <TextBlock Text="Your flashcard workspace is ready. Create decks, generate cards, and review due cards."
                                                           Foreground="{StaticResource MutedBrush}"
                                                           FontSize="15"
                                                           TextWrapping="Wrap"
                                                           Margin="0,10,0,20"/>

                                                <StackPanel Orientation="Horizontal">
                                                    <Button Content="Generate Cards" Style="{StaticResource PremiumButton}" Click="GeneratePage_Click" Width="150"/>
                                                    <Button Content="Study Now" Style="{StaticResource SoftButton}" Click="StudyPage_Click" Width="120"/>
                                                </StackPanel>
                                            </StackPanel>

                                            <Image Grid.Column="1"
                                                   Source="/Assets/Animations/Study.gif"
                                                   Width="150"
                                                   Height="150"
                                                   Stretch="Uniform"
                                                   HorizontalAlignment="Right"/>
                                        </Grid>
                                    </Border>

                                    <Border Grid.Column="1" Style="{StaticResource CardBorder}" Padding="22">
                                        <StackPanel>
                                            <TextBlock Text="Study streak" FontSize="18" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                            <StackPanel Orientation="Horizontal" Margin="0,14,0,0">
                                                <Image Source="/Assets/Animations/streakfire.gif" Width="64" Height="64" Stretch="Uniform" Margin="0,0,14,0"/>
                                                <StackPanel VerticalAlignment="Center">
                                                    <TextBlock x:Name="StatsStreak" Text="0 days" Foreground="{StaticResource WarningBrush}" FontSize="28" FontWeight="Bold"/>
                                                    <TextBlock Text="Keep reviewing daily" Foreground="{StaticResource MutedBrush}"/>
                                                </StackPanel>
                                            </StackPanel>
                                        </StackPanel>
                                    </Border>
                                </Grid>

                                <UniformGrid Columns="4" Margin="0,0,0,18">
                                    <Border Style="{StaticResource CardBorder}" Margin="0,0,14,0">
                                        <StackPanel>
                                            <Border Background="{StaticResource PrimarySoftBrush}" Width="42" Height="42" CornerRadius="10" HorizontalAlignment="Left">
                                                <TextBlock Text="🗂️" FontSize="22" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            </Border>
                                            <TextBlock x:Name="StatsDecks" Text="0" Foreground="{StaticResource TextBrush}" FontSize="32" FontWeight="Bold" Margin="0,12,0,0"/>
                                            <TextBlock Text="Decks" Foreground="{StaticResource MutedBrush}"/>
                                        </StackPanel>
                                    </Border>

                                    <Border Style="{StaticResource CardBorder}" Margin="0,0,14,0">
                                        <StackPanel>
                                            <Border Background="{StaticResource InfoSoftBrush}" Width="42" Height="42" CornerRadius="10" HorizontalAlignment="Left">
                                                <TextBlock Text="🃏" FontSize="22" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            </Border>
                                            <TextBlock x:Name="StatsTotalCards" Text="0" Foreground="{StaticResource TextBrush}" FontSize="32" FontWeight="Bold" Margin="0,12,0,0"/>
                                            <TextBlock Text="Total cards" Foreground="{StaticResource MutedBrush}"/>
                                        </StackPanel>
                                    </Border>

                                    <Border Style="{StaticResource CardBorder}" Margin="0,0,14,0">
                                        <StackPanel>
                                            <Border Background="{StaticResource SuccessSoftBrush}" Width="42" Height="42" CornerRadius="10" HorizontalAlignment="Left">
                                                <TextBlock Text="✅" FontSize="22" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            </Border>
                                            <TextBlock x:Name="StatsDueCards" Text="0" Foreground="{StaticResource SuccessBrush}" FontSize="32" FontWeight="Bold" Margin="0,12,0,0"/>
                                            <TextBlock Text="Due today" Foreground="{StaticResource MutedBrush}"/>
                                        </StackPanel>
                                    </Border>

                                    <Border Style="{StaticResource CardBorder}">
                                        <StackPanel>
                                            <Border Background="{StaticResource WarningSoftBrush}" Width="42" Height="42" CornerRadius="10" HorizontalAlignment="Left">
                                                <TextBlock Text="📚" FontSize="22" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            </Border>
                                            <TextBlock x:Name="StatsStudiedToday" Text="0" Foreground="{StaticResource PrimaryBrush}" FontSize="32" FontWeight="Bold" Margin="0,12,0,0"/>
                                            <TextBlock Text="Studied today" Foreground="{StaticResource MutedBrush}"/>
                                        </StackPanel>
                                    </Border>
                                </UniformGrid>

                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="0.58*"/>
                                        <ColumnDefinition Width="0.42*"/>
                                    </Grid.ColumnDefinitions>

                                    <Border Style="{StaticResource CardBorder}" Margin="0,0,18,0">
                                        <StackPanel>
                                            <TextBlock Text="Study performance" FontSize="20" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                            <TextBlock Text="Your progress summary across all local decks."
                                                       Foreground="{StaticResource MutedBrush}"
                                                       Margin="0,4,0,18"/>

                                            <Grid>
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="*"/>
                                                    <ColumnDefinition Width="*"/>
                                                    <ColumnDefinition Width="*"/>
                                                </Grid.ColumnDefinitions>

                                                <Border Grid.Column="0" Background="{StaticResource PrimarySoftBrush}" CornerRadius="12" Padding="18" Margin="0,0,12,0">
                                                    <StackPanel>
                                                        <TextBlock Text="Accuracy" Foreground="{StaticResource MutedBrush}"/>
                                                        <TextBlock x:Name="StatsAccuracy" Text="0%" Foreground="{StaticResource PrimaryBrush}" FontSize="30" FontWeight="Bold"/>
                                                    </StackPanel>
                                                </Border>

                                                <Border Grid.Column="1" Background="{StaticResource DangerSoftBrush}" CornerRadius="12" Padding="18" Margin="0,0,12,0">
                                                    <StackPanel>
                                                        <TextBlock Text="Weak cards" Foreground="{StaticResource MutedBrush}"/>
                                                        <TextBlock x:Name="StatsWeak" Text="0" Foreground="{StaticResource DangerBrush}" FontSize="30" FontWeight="Bold"/>
                                                    </StackPanel>
                                                </Border>

                                                <Border Grid.Column="2" Background="{StaticResource SuccessSoftBrush}" CornerRadius="12" Padding="18">
                                                    <StackPanel>
                                                        <TextBlock Text="Mode" Foreground="{StaticResource MutedBrush}"/>
                                                        <TextBlock Text="SRS" Foreground="{StaticResource SuccessBrush}" FontSize="30" FontWeight="Bold"/>
                                                    </StackPanel>
                                                </Border>
                                            </Grid>
                                        </StackPanel>
                                    </Border>

                                    <Border Grid.Column="1" Style="{StaticResource CardBorder}">
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="92"/>
                                                <ColumnDefinition Width="*"/>
                                            </Grid.ColumnDefinitions>

                                            <Image Source="/Assets/Animations/Success.gif" Width="74" Height="74" Stretch="Uniform"/>

                                            <StackPanel Grid.Column="1">
                                                <TextBlock Text="Recommended next step" FontSize="18" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                                <TextBlock Text="Create a topic deck, paste your notes, then generate cards with Z.ai."
                                                           Foreground="{StaticResource MutedBrush}"
                                                           TextWrapping="Wrap"
                                                           Margin="0,8,0,14"/>
                                                <Button Content="Go to Generate" Style="{StaticResource PremiumButton}" Click="GeneratePage_Click" Width="140"/>
                                            </StackPanel>
                                        </Grid>
                                    </Border>
                                </Grid>
                            </StackPanel>
                        </ScrollViewer>
                    </Grid>

                    <!-- GENERATE -->
                    <Grid x:Name="PageGenerate" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="70"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="Generate Flashcards" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Choose a deck, paste notes, and generate high-yield flashcards with Z.ai."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <WrapPanel Grid.Row="1" VerticalAlignment="Center">
                            <TextBlock Text="Deck" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <ComboBox x:Name="GenerateDeckCombo" Width="190" Margin="0,0,14,0" SelectionChanged="DeckSelector_SelectionChanged"/>

                            <TextBlock Text="Mode" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <ComboBox x:Name="ModeCombo" Width="170" Margin="0,0,14,0"/>

                            <TextBlock Text="Difficulty" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <ComboBox x:Name="DifficultyCombo" Width="130" Margin="0,0,14,0"/>

                            <TextBlock Text="Answer" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <ComboBox x:Name="AnswerLengthCombo" Width="120" Margin="0,0,14,0"/>

                            <TextBlock Text="Count" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <ComboBox x:Name="CountCombo" Width="90" Margin="0,0,14,0"/>

                            <TextBlock Text="Language" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <ComboBox x:Name="LanguageCombo" Width="210"/>
                        </WrapPanel>

                        <Grid Grid.Row="2">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="0.66*"/>
                                <ColumnDefinition Width="0.34*"/>
                            </Grid.ColumnDefinitions>

                            <Border Style="{StaticResource CardBorder}" Margin="0,0,18,0">
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="42"/>
                                        <RowDefinition Height="*"/>
                                        <RowDefinition Height="60"/>
                                    </Grid.RowDefinitions>

                                    <TextBlock Text="Source Material" FontSize="19" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                    <TextBox x:Name="SourceBox" Grid.Row="1" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"/>

                                    <StackPanel Grid.Row="2" Orientation="Horizontal" VerticalAlignment="Bottom">
                                        <Button Content="Generate with Z.ai" Style="{StaticResource PremiumButton}" Click="GenerateAutomatic_Click" Width="170"/>
                                        <Button Content="Create Manual Prompt" Style="{StaticResource SoftButton}" Click="CreatePrompt_Click" Width="190"/>
                                        <Button Content="Clear" Style="{StaticResource SoftButton}" Background="#ECEEF1" Foreground="{StaticResource MutedBrush}" Click="ClearSource_Click" Width="90"/>
                                    </StackPanel>
                                </Grid>
                            </Border>

                            <Border Grid.Column="1" Style="{StaticResource CardBorder}">
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="42"/>
                                        <RowDefinition Height="96"/>
                                        <RowDefinition Height="*"/>
                                        <RowDefinition Height="60"/>
                                    </Grid.RowDefinitions>

                                    <TextBlock Text="Manual Prompt Backup" FontSize="19" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>

                                    <Border Grid.Row="1" Background="{StaticResource PrimarySoftBrush}" CornerRadius="14" Padding="12" Margin="0,0,0,12">
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="70"/>
                                                <ColumnDefinition Width="*"/>
                                            </Grid.ColumnDefinitions>

                                            <Image Source="/Assets/Animations/Loading.gif" Width="58" Height="58" Stretch="Uniform"/>
                                            <TextBlock Grid.Column="1"
                                                       Text="Automatic generation is best. Manual prompt is only a backup."
                                                       Foreground="{StaticResource PrimaryBrush}"
                                                       TextWrapping="Wrap"
                                                       VerticalAlignment="Center"/>
                                        </Grid>
                                    </Border>

                                    <TextBox x:Name="PromptBox" Grid.Row="2" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"/>
                                    <Button Grid.Row="3" Content="Copy Prompt" Style="{StaticResource PremiumButton}" Click="CopyPrompt_Click" Width="140" VerticalAlignment="Bottom"/>
                                </Grid>
                            </Border>
                        </Grid>
                    </Grid>

                    <!-- IMPORT -->
                    <Grid x:Name="PageImport" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="62"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="Import AI JSON" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Paste JSON generated by any AI model. Cards will be added to the selected deck."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <StackPanel Grid.Row="1" Orientation="Horizontal" VerticalAlignment="Center">
                            <TextBlock Text="Import into deck:" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,10,0"/>
                            <ComboBox x:Name="ImportDeckCombo" Width="280" SelectionChanged="DeckSelector_SelectionChanged"/>
                        </StackPanel>

                        <Grid Grid.Row="2">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="0.66*"/>
                                <ColumnDefinition Width="0.34*"/>
                            </Grid.ColumnDefinitions>

                            <Border Style="{StaticResource CardBorder}" Margin="0,0,18,0">
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="42"/>
                                        <RowDefinition Height="*"/>
                                        <RowDefinition Height="60"/>
                                    </Grid.RowDefinitions>

                                    <TextBlock Text="Paste JSON" FontSize="19" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                    <TextBox x:Name="ImportBox" Grid.Row="1" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"/>

                                    <StackPanel Grid.Row="2" Orientation="Horizontal" VerticalAlignment="Bottom">
                                        <Button Content="Import Cards" Style="{StaticResource PremiumButton}" Click="ImportCards_Click" Width="140"/>
                                        <Button Content="Clear" Style="{StaticResource SoftButton}" Background="#ECEEF1" Foreground="{StaticResource MutedBrush}" Click="ClearImport_Click" Width="90"/>
                                    </StackPanel>
                                </Grid>
                            </Border>

                            <Border Grid.Column="1" Style="{StaticResource CardBorder}">
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="42"/>
                                        <RowDefinition Height="86"/>
                                        <RowDefinition Height="*"/>
                                        <RowDefinition Height="34"/>
                                    </Grid.RowDefinitions>

                                    <TextBlock Text="Current Deck Cards" FontSize="19" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>

                                    <Border Grid.Row="1" Background="{StaticResource InfoSoftBrush}" CornerRadius="14" Padding="12" Margin="0,0,0,12">
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="60"/>
                                                <ColumnDefinition Width="*"/>
                                            </Grid.ColumnDefinitions>

                                            <Image Source="/Assets/Animations/Upload.gif" Width="50" Height="50" Stretch="Uniform"/>
                                            <TextBlock Grid.Column="1"
                                                       Text="Import cards into the active deck."
                                                       Foreground="{StaticResource MutedBrush}"
                                                       TextWrapping="Wrap"
                                                       VerticalAlignment="Center"/>
                                        </Grid>
                                    </Border>

                                    <ListBox x:Name="ImportedList" Grid.Row="2" SelectionChanged="ImportedList_SelectionChanged"/>
                                    <TextBlock x:Name="ImportSummaryText" Grid.Row="3" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center"/>
                                </Grid>
                            </Border>
                        </Grid>
                    </Grid>

                    <!-- DECKS -->
                    <Grid x:Name="PageDecks" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="74"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="My Decks" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Create decks, search cards, move cards, and start focused study sessions."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <WrapPanel Grid.Row="1" VerticalAlignment="Center">
                            <TextBox x:Name="DeckNameBox" Width="250" Height="40" Margin="0,0,10,0"/>
                            <Button Content="Create Deck" Style="{StaticResource PremiumButton}" Background="{StaticResource SuccessBrush}" Click="CreateDeck_Click" Width="130"/>
                            <Button Content="Rename" Style="{StaticResource SoftButton}" Click="RenameDeck_Click" Width="110"/>
                            <Button Content="Delete Deck" Style="{StaticResource PremiumButton}" Background="{StaticResource DangerBrush}" Click="DeleteDeck_Click" Width="130"/>

                            <TextBlock Text="Search" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="24,0,8,0"/>
                            <TextBox x:Name="SearchBox" Width="220" Height="40" TextChanged="SearchFilter_Changed"/>

                            <TextBlock Text="Tag" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="16,0,8,0"/>
                            <TextBox x:Name="TagFilterBox" Width="160" Height="40" TextChanged="SearchFilter_Changed"/>

                            <CheckBox x:Name="DueOnlyCheck" Content="Due only" VerticalAlignment="Center" Margin="16,0,0,0" Checked="DueOnly_Checked" Unchecked="DueOnly_Checked"/>
                        </WrapPanel>

                        <Grid Grid.Row="2">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="0.32*"/>
                                <ColumnDefinition Width="0.68*"/>
                            </Grid.ColumnDefinitions>

                            <Border Style="{StaticResource CardBorder}" Margin="0,0,18,0">
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="42"/>
                                        <RowDefinition Height="*"/>
                                        <RowDefinition Height="72"/>
                                    </Grid.RowDefinitions>

                                    <TextBlock Text="Deck Library" FontSize="19" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                    <ListBox x:Name="DeckList" Grid.Row="1" SelectionChanged="DeckList_SelectionChanged"/>

                                    <StackPanel Grid.Row="2" Orientation="Horizontal" VerticalAlignment="Center">
                                        <Button Content="Study" Style="{StaticResource PremiumButton}" Background="{StaticResource SuccessBrush}" Click="StudySelectedDeck_Click" Width="95"/>
                                        <Button Content="Export" Style="{StaticResource SoftButton}" Click="ExportSelectedDeck_Click" Width="95"/>
                                    </StackPanel>
                                </Grid>
                            </Border>

                            <Border Grid.Column="1" Style="{StaticResource CardBorder}">
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="42"/>
                                        <RowDefinition Height="*"/>
                                        <RowDefinition Height="76"/>
                                    </Grid.RowDefinitions>

                                    <TextBlock x:Name="DeckSummaryText" FontSize="19" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                    <ListBox x:Name="CardsList" Grid.Row="1" SelectionChanged="CardsList_SelectionChanged"/>

                                    <WrapPanel Grid.Row="2" VerticalAlignment="Center">
                                        <Button Content="Preview Selected" Style="{StaticResource SoftButton}" Click="PreviewSelected_Click" Width="160"/>
                                        <Button Content="Delete Selected" Style="{StaticResource PremiumButton}" Background="{StaticResource DangerBrush}" Click="DeleteSelected_Click" Width="150"/>

                                        <TextBlock Text="Move to" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="18,0,8,0"/>
                                        <ComboBox x:Name="MoveDeckCombo" Width="210"/>
                                        <Button Content="Move" Style="{StaticResource SoftButton}" Click="MoveSelected_Click" Width="90"/>
                                    </WrapPanel>
                                </Grid>
                            </Border>
                        </Grid>
                    </Grid>

                    <!-- STUDY -->
                    <Grid x:Name="PageStudy" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="62"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="Study Mode" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Reveal the answer, then rate your memory with Again / Hard / Good / Easy."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <StackPanel Grid.Row="1" Orientation="Horizontal" VerticalAlignment="Center">
                            <TextBlock Text="Study deck:" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,10,0"/>
                            <ComboBox x:Name="StudyDeckCombo" Width="280" SelectionChanged="DeckSelector_SelectionChanged"/>
                            <Button Content="Restart Session" Style="{StaticResource SoftButton}" Click="StudySelectedDeck_Click" Width="155" Margin="16,0,0,0"/>
                        </StackPanel>

                        <Border Grid.Row="2" Style="{StaticResource CardBorder}" Margin="70,12" Padding="34">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="44"/>
                                    <RowDefinition Height="*"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="82"/>
                                </Grid.RowDefinitions>

                                <TextBlock x:Name="StudyProgressText" Text="No cards" Foreground="{StaticResource MutedBrush}" FontSize="15"/>

                                <Border Grid.Row="1" Background="{StaticResource SoftCardBrush}" CornerRadius="18" Padding="36">
                                    <StackPanel VerticalAlignment="Center">
                                        <TextBlock x:Name="StudyFrontText"
                                                   Text="No card selected."
                                                   TextWrapping="Wrap"
                                                   Foreground="{StaticResource TextBrush}"
                                                   FontSize="28"
                                                   FontWeight="SemiBold"
                                                   TextAlignment="Center"/>

                                        <Border x:Name="StudyAnswerPanel"
                                                Visibility="Collapsed"
                                                Background="{StaticResource CardBrush}"
                                                CornerRadius="16"
                                                Padding="26"
                                                Margin="0,34,0,0">
                                            <TextBlock x:Name="StudyBackText"
                                                       TextWrapping="Wrap"
                                                       Foreground="{StaticResource SuccessBrush}"
                                                       FontSize="22"
                                                       TextAlignment="Center"/>
                                        </Border>
                                    </StackPanel>
                                </Border>

                                <TextBlock x:Name="StudyHintText" Grid.Row="2" Foreground="{StaticResource MutedBrush}" FontSize="13" Margin="0,16,0,0" TextAlignment="Center"/>

                                <StackPanel Grid.Row="3" Orientation="Horizontal" HorizontalAlignment="Center" VerticalAlignment="Center">
                                    <Button Content="Show Answer" Style="{StaticResource PremiumButton}" Click="ShowAnswer_Click" Width="150"/>
                                    <Button Content="Again" Style="{StaticResource PremiumButton}" Background="{StaticResource DangerBrush}" Click="Again_Click" Width="100"/>
                                    <Button Content="Hard" Style="{StaticResource PremiumButton}" Background="{StaticResource WarningBrush}" Click="Hard_Click" Width="100"/>
                                    <Button Content="Good" Style="{StaticResource PremiumButton}" Background="{StaticResource SuccessBrush}" Click="Good_Click" Width="100"/>
                                    <Button Content="Easy" Style="{StaticResource PremiumButton}" Background="{StaticResource InfoBrush}" Click="Easy_Click" Width="100"/>
                                </StackPanel>
                            </Grid>
                        </Border>
                    </Grid>

                    <!-- PREVIEW -->
                    <Grid x:Name="PagePreview" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="Preview / Edit" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Edit the selected card’s front, back, and tags."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <Border Grid.Row="1" Style="{StaticResource CardBorder}">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="42"/>
                                    <RowDefinition Height="0.42*"/>
                                    <RowDefinition Height="0.42*"/>
                                    <RowDefinition Height="70"/>
                                    <RowDefinition Height="74"/>
                                    <RowDefinition Height="58"/>
                                </Grid.RowDefinitions>

                                <TextBlock x:Name="CardCounterText" FontSize="19" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>

                                <GroupBox Grid.Row="1" Header="Front" Margin="0,0,0,10">
                                    <TextBox x:Name="FrontBox" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"/>
                                </GroupBox>

                                <GroupBox Grid.Row="2" Header="Back" Margin="0,0,0,10">
                                    <TextBox x:Name="BackBox" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"/>
                                </GroupBox>

                                <StackPanel Grid.Row="3" Orientation="Horizontal" VerticalAlignment="Center">
                                    <Button Content="Previous" Style="{StaticResource SoftButton}" Click="Previous_Click" Width="110"/>
                                    <Button Content="Next" Style="{StaticResource SoftButton}" Click="Next_Click" Width="90"/>
                                    <Button Content="Save" Style="{StaticResource PremiumButton}" Click="SaveCard_Click" Width="90"/>
                                    <Button Content="Delete" Style="{StaticResource PremiumButton}" Background="{StaticResource DangerBrush}" Click="DeleteCard_Click" Width="100"/>
                                    <Button Content="Copy Current" Style="{StaticResource SoftButton}" Click="CopyCurrent_Click" Width="150"/>
                                </StackPanel>

                                <GroupBox Grid.Row="4" Header="Tags" Margin="0,0,0,10">
                                    <TextBox x:Name="TagsBox"/>
                                </GroupBox>

                                <StackPanel Grid.Row="5" Orientation="Horizontal" VerticalAlignment="Center">
                                    <Button Content="Copy All For Anki" Style="{StaticResource PremiumButton}" Background="{StaticResource SuccessBrush}" Click="CopyAll_Click" Width="180"/>
                                    <Button Content="Export Selected Deck" Style="{StaticResource SoftButton}" Click="ExportSelectedDeck_Click" Width="180"/>
                                </StackPanel>
                            </Grid>
                        </Border>
                    </Grid>

                    <!-- EXPORT -->
                    <Grid x:Name="PageExport" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="62"/>
                            <RowDefinition Height="*"/>
                            <RowDefinition Height="70"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="Export" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Export selected deck or all decks as tab-separated fields for Anki."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <StackPanel Grid.Row="1" Orientation="Horizontal" VerticalAlignment="Center">
                            <TextBlock Text="Export deck:" Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,10,0"/>
                            <ComboBox x:Name="ExportDeckCombo" Width="280" SelectionChanged="DeckSelector_SelectionChanged"/>
                        </StackPanel>

                        <Border Grid.Row="2" Style="{StaticResource CardBorder}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="220"/>
                                </Grid.ColumnDefinitions>

                                <TextBox x:Name="ExportPreviewBox" IsReadOnly="True" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"/>

                                <Border Grid.Column="1" Background="{StaticResource SuccessSoftBrush}" CornerRadius="14" Padding="16" Margin="18,0,0,0">
                                    <StackPanel VerticalAlignment="Center">
                                        <Image Source="/Assets/Animations/exportdownload.gif" Width="90" Height="90" Stretch="Uniform" HorizontalAlignment="Center"/>
                                        <TextBlock Text="Export ready" FontSize="18" FontWeight="Bold" Foreground="{StaticResource TextBrush}" TextAlignment="Center" Margin="0,10,0,4"/>
                                        <TextBlock Text="Copy or save your cards for Anki."
                                                   Foreground="{StaticResource MutedBrush}"
                                                   TextWrapping="Wrap"
                                                   TextAlignment="Center"/>
                                    </StackPanel>
                                </Border>
                            </Grid>
                        </Border>

                        <StackPanel Grid.Row="3" Orientation="Horizontal" VerticalAlignment="Center">
                            <Button Content="Refresh Preview" Style="{StaticResource SoftButton}" Click="RefreshExport_Click" Width="150"/>
                            <Button Content="Copy Selected Deck" Style="{StaticResource PremiumButton}" Background="{StaticResource SuccessBrush}" Click="CopySelectedDeck_Click" Width="180"/>
                            <Button Content="Copy All Decks" Style="{StaticResource SoftButton}" Click="CopyAll_Click" Width="150"/>
                            <Button Content="Export Selected" Style="{StaticResource PremiumButton}" Click="ExportSelectedDeck_Click" Width="155"/>
                            <Button Content="Export All" Style="{StaticResource SoftButton}" Click="ExportAllDecks_Click" Width="120"/>
                        </StackPanel>
                    </Grid>

                    <!-- SETTINGS -->
                    <Grid x:Name="PageSettings" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="AI Settings" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Configure Z.ai generation for local testing."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
                            <StackPanel>
                                <Border Style="{StaticResource CardBorder}" Padding="22" Margin="0,0,0,18">
                                    <StackPanel MaxWidth="900" HorizontalAlignment="Left">
                                        <StackPanel Orientation="Horizontal" Margin="0,0,0,20">
                                            <Border Width="50" Height="50" Background="{StaticResource PrimarySoftBrush}" CornerRadius="12" Margin="0,0,14,0">
                                                <TextBlock Text="🔑" FontSize="24" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            </Border>
                                            <StackPanel VerticalAlignment="Center">
                                                <TextBlock Text="Z.ai API" FontSize="22" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                                <TextBlock Text="Saved locally on this computer." Foreground="{StaticResource MutedBrush}"/>
                                            </StackPanel>
                                        </StackPanel>

                                        <TextBlock Text="API Key" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                                        <PasswordBox x:Name="ApiKeyBox" Height="44" Margin="0,8,0,16"/>

                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="*"/>
                                            </Grid.ColumnDefinitions>

                                            <StackPanel Grid.Column="0" Margin="0,0,10,0">
                                                <TextBlock Text="Model" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                                                <TextBox x:Name="ModelBox" Height="44" Margin="0,8,0,16"/>
                                            </StackPanel>

                                            <StackPanel Grid.Column="1" Margin="10,0,0,0">
                                                <TextBlock Text="Base URL" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                                                <TextBox x:Name="BaseUrlBox" Height="44" Margin="0,8,0,16"/>
                                            </StackPanel>
                                        </Grid>

                                        <StackPanel Orientation="Horizontal">
                                            <Button Content="Save Settings" Style="{StaticResource PremiumButton}" Background="{StaticResource SuccessBrush}" Click="SaveSettings_Click" Width="150"/>
                                            <Button Content="Clear Key" Style="{StaticResource PremiumButton}" Background="{StaticResource DangerBrush}" Click="ClearKey_Click" Width="120"/>
                                        </StackPanel>
                                    </StackPanel>
                                </Border>

                                <Border Background="{StaticResource WarningSoftBrush}" CornerRadius="12" Padding="18">
                                    <TextBlock Text="Important: this local API key setup is for testing. The final paid app should use a backend so the API key is hidden from users."
                                               Foreground="{StaticResource WarningBrush}"
                                               TextWrapping="Wrap"
                                               FontWeight="SemiBold"/>
                                </Border>
                            </StackPanel>
                        </ScrollViewer>
                    </Grid>

                    <!-- ACCOUNT -->
                    <Grid x:Name="PageAccount" Visibility="Collapsed">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="76"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel>
                            <TextBlock Text="Account" FontSize="30" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                            <TextBlock Text="Local demo account and subscription information."
                                       Foreground="{StaticResource MutedBrush}"/>
                        </StackPanel>

                        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
                            <StackPanel>
                                <Border Style="{StaticResource CardBorder}" Padding="22" Margin="0,0,0,18">
                                    <StackPanel>
                                        <StackPanel Orientation="Horizontal" Margin="0,0,0,22">
                                            <Border Width="64" Height="64" Background="{StaticResource PrimarySoftBrush}" CornerRadius="16" Margin="0,0,16,0">
                                                <TextBlock Text="👤" FontSize="32" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            </Border>

                                            <StackPanel VerticalAlignment="Center">
                                                <TextBlock Text="Subscription Details" FontSize="22" FontWeight="Bold" Foreground="{StaticResource TextBrush}"/>
                                                <TextBlock Text="Local demo activation. Backend comes later." Foreground="{StaticResource MutedBrush}"/>
                                            </StackPanel>
                                        </StackPanel>

                                        <Grid Margin="0,0,0,22">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="*"/>
                                            </Grid.ColumnDefinitions>

                                            <Border Grid.Column="0" Background="{StaticResource PrimarySoftBrush}" CornerRadius="12" Padding="16" Margin="0,0,12,0">
                                                <TextBlock x:Name="AccountEmailText" Foreground="{StaticResource PrimaryBrush}" TextWrapping="Wrap"/>
                                            </Border>

                                            <Border Grid.Column="1" Background="{StaticResource SuccessSoftBrush}" CornerRadius="12" Padding="16" Margin="0,0,12,0">
                                                <TextBlock x:Name="AccountPlanText" Foreground="{StaticResource SuccessBrush}" TextWrapping="Wrap"/>
                                            </Border>

                                            <Border Grid.Column="2" Background="{StaticResource InfoSoftBrush}" CornerRadius="12" Padding="16">
                                                <TextBlock x:Name="AccountExpiryText" Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap"/>
                                            </Border>
                                        </Grid>

                                        <TextBlock Text="Apply new activation code" Foreground="{StaticResource MutedBrush}" FontWeight="SemiBold"/>
                                        <TextBox x:Name="ApplyCodeBox" Height="44" Margin="0,8,0,16"/>

                                        <Button Content="Apply Code" Style="{StaticResource PremiumButton}" Background="{StaticResource SuccessBrush}" Click="ApplyCode_Click" Width="130"/>
                                    </StackPanel>
                                </Border>

                                <Border Background="{StaticResource DangerSoftBrush}" CornerRadius="12" Padding="18">
                                    <TextBlock Text="This is not a real online subscription system yet. V8 will include backend login, real activation codes, and secure payment/subscription checks."
                                               Foreground="{StaticResource DangerBrush}"
                                               TextWrapping="Wrap"
                                               FontWeight="SemiBold"/>
                                </Border>
                            </StackPanel>
                        </ScrollViewer>
                    </Grid>

                </Grid>

                <!-- STATUS BAR -->
                <Border Grid.Row="2" Background="{StaticResource CardBrush}" Padding="16,0">
                    <TextBlock x:Name="StatusText" Text="Ready." Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center"/>
                </Border>
            </Grid>
        </Grid>

        <!-- LOADING OVERLAY -->
        <Grid x:Name="LoadingOverlay" Background="#88000000" Visibility="Collapsed">
            <Border Background="{StaticResource CardBrush}"
                    CornerRadius="18"
                    Padding="34"
                    Width="410"
                    HorizontalAlignment="Center"
                    VerticalAlignment="Center">
                <StackPanel HorizontalAlignment="Center">
                    <Image Source="/Assets/Animations/Loading.gif"
                           Width="112"
                           Height="112"
                           Stretch="Uniform"
                           HorizontalAlignment="Center"
                           Margin="0,0,0,14"/>

                    <TextBlock x:Name="LoadingMessageText"
                               Text="Working..."
                               Foreground="{StaticResource TextBrush}"
                               FontSize="23"
                               FontWeight="Bold"
                               HorizontalAlignment="Center"/>

                    <TextBlock Text="Please wait while the app processes your request."
                               Foreground="{StaticResource MutedBrush}"
                               TextAlignment="Center"
                               TextWrapping="Wrap"
                               Margin="0,12,0,0"/>
                </StackPanel>
            </Border>
        </Grid>

    </Grid>
</Window>
