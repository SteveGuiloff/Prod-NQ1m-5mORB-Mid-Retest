//////////////////////////////////////
///  Orb retest Version 1.1 DEV. 
//////////////////////////////////////
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ORB5mdev : Strategy
    {
        private enum RobotState { WaitingMarket, BuildingORB, WaitingBreakout, SearchingEntry, OrderPending, InTrade, SessionClosed }
        private RobotState currentRobotState = RobotState.WaitingMarket;

        private double orbUpper, orbLower, orbMid;
        private bool orbDefined, tradeExecutedToday, beTriggered;
        private int breakoutDir;
        private DateTime lastResetDate;
        private Order entryOrder = null;
        
        private Button panicButton;
        private Grid chartGrid;
        private NinjaTrader.Gui.Tools.SimpleFont myFont = new NinjaTrader.Gui.Tools.SimpleFont("Courier New", 14) { Bold = true };
		private int contratosCalculados = 0; // Variable global
		private double stopPrecioFinal = 0;
		private double targetPrecioFinal = 0;
		private double stopDistanciaTicks = 0; //

        #region Parámetros
        [NinjaScriptProperty]
        [Display(Name = "Delta Breakout", GroupName = "1. ORB", Order = 0)]
        public double DeltaInput { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min ORB Size (Handles)", GroupName = "1. ORB", Order = 1)]
        public double MinOrbHandles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Riesgo USD por Trade", GroupName = "2. Riesgo", Order = 0)]
        public double RiskUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profit Ratio (R:R)", GroupName = "2. Riesgo", Order = 1)]
        public double RatioR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Activar Break Even en Ratio", GroupName = "2. Riesgo", Order = 2)]
        public double BeTriggerRatio { get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="PAR-07: BeOffset (Pts)", Description="Puntos por delante de la entrada para proteger el trade al activar BE", Order=7, GroupName="02. Salidas y BE")]
		public double BeOffset
		{ get; set; }

		[NinjaScriptProperty]
        [Display(Name = "Fin Ventana Trading (HHMM)", GroupName = "3. Tiempo", Order = 0)]
        public int TradingEndHHMM { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cierre Forzoso (HHMM)", GroupName = "3. Tiempo", Order = 1)]
        public int ForceCloseHHMM { get; set; }
		
		[NinjaScriptProperty]
        [Display(Name = "Cierre Forzoso (HHMM)", GroupName = "3. Tiempo", Order = 2)]
        public int MaxBreakoutTimeHHMM { get; set; }
		
		
		[NinjaScriptProperty]
		[Display(Name="Latencia Máxima (ms)", GroupName="Parámetros", Order=13)]
		public int latencia { get; set; } = 500;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ORB5m_Development";
                Calculate = Calculate.OnBarClose;
                IsExitOnSessionCloseStrategy = true;
                DeltaInput = 7.5; MinOrbHandles = 35.0; RiskUsd = 3000.0; RatioR = 2.2;
                BeTriggerRatio = 1.0; TradingEndHHMM = 1200; ForceCloseHHMM = 1556;
				BeOffset = 3.5;
				latencia = 500; // Valor inicial por defecto: 500ms
				MaxBreakoutTimeHHMM=01145;
				
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);
				// Nueva serie para el Break Even (BarsArray[2]): 1 Segundo
    			AddDataSeries(BarsPeriodType.Second, 1);
            }
            else if (State == State.Terminated)
            {
                RemovePanicButton();
            }
        }

protected override void OnBarUpdate()
{
    // 0. SEGURIDAD: Validar que todas las series tengan datos cargados
    if (CurrentBars[0] < 5 || CurrentBars[1] < 1 || (BarsArray.Length > 2 && CurrentBars[2] < 1)) return;

    // 1. GESTIÓN DE INTERFAZ (Botón Panic)
    if (State == State.Realtime && panicButton == null) CreatePanicButton();

    // =========================================================================
    // A. LÓGICA DE 1 SEGUNDO (BarsInProgress == 2)
    // =========================================================================
    if (BarsInProgress == 2)
    {
        // Solo ejecutamos el CheckBreakEven aquí para máxima sensibilidad
        if (currentRobotState == RobotState.InTrade && !beTriggered && Position.MarketPosition != MarketPosition.Flat)
        {
            CheckBreakEven();
        }
        return; // Salimos: no queremos que el resto del código corra cada segundo
    }

	// =========================================================================
    // B. LÓGICA DE 5 MINUTOS (BarsInProgress == 1)
    // =========================================================================
    if (BarsInProgress == 1)
    {
        int hhmm5 = ToTime(Time[0]);
        
        // Entramos a evaluar solo si el robot está esperando construir el ORB
        if (currentRobotState == RobotState.BuildingORB)
        {
            // CANDADO 1: Retraso Estructural o Encendido Tardío
            // Si el código se ejecuta y ya son las 09:40 o más, el ORB ya no es válido.
            if (hhmm5 >= 094000)
            {
                string errorMsg = string.Format("LOG-ERR-01: Bloqueo de seguridad. Intento de construcción ORB tardío a las {0}. Posible lag o encendido fuera de hora.", hhmm5);
                ChangeRobotState(RobotState.SessionClosed, errorMsg);
                return;
            }

            // Captura del ORB exactamente al cierre de la vela que abre 09:30 y cierra 09:35
            if (hhmm5 >= 093500)
            {
                // CANDADO 2: Validación de Sincronía de la Barra
                // Verificamos que la barra que acaba de cerrar sea EXACTAMENTE la de las 09:30
                if (Time[0].Hour != 9 || Time[0].Minute != 35)
                {
                    string errorMsg = string.Format("LOG-ERR-02: Discrepancia horaria. La barra actual ({0:HH:mm}) no corresponde a la apertura oficial (09:30).", Time[0]);
                    ChangeRobotState(RobotState.SessionClosed, errorMsg);
                    return;
                }

                // Si pasa los candados, procedemos con los niveles
                orbUpper = High[0]; 
                orbLower = Low[0]; 
                orbMid = (orbUpper + orbLower) / 2;

                double mitadRango = Math.Abs(orbUpper - orbLower) / 2.0;
                stopDistanciaTicks = Math.Ceiling(mitadRango / TickSize) * TickSize;
                
                contratosCalculados = (int)Math.Max(1, Math.Floor(RiskUsd / (stopDistanciaTicks * Instrument.MasterInstrument.PointValue)));

                if ((orbUpper - orbLower) >= MinOrbHandles) 
                {
                    orbDefined = true;
                    string msg = string.Format("LOG-01: Niveles fijados: H: {0} L: {1} Mid: {2} Size: {3} Contratos: {4} SL Ticks: {5}", 
                                 orbUpper, orbLower, orbMid, (orbUpper - orbLower), contratosCalculados, stopDistanciaTicks);
                    ChangeRobotState(RobotState.WaitingBreakout, msg);
                }
                else 
                {
                    ChangeRobotState(RobotState.SessionClosed, "LOG-01-RECHAZADO: Rango ORB insuficiente para operar.");
                }
            }
        }
        return; 
    }
    // =========================================================================
    // C. LÓGICA DE 1 MINUTO (BarsInProgress == 0)
    // =========================================================================
    if (BarsInProgress == 0)
    {
        int hhmm = ToTime(Time[0]);
        DateTime currentDay = Time[0].Date;

        // 1. Reset Diario
        if (currentDay != lastResetDate)
        {
            ResetStrategy(currentDay);
            ChangeRobotState(RobotState.WaitingMarket, "Reiniciando estrategia - Esperando apertura");
        }

        // 2. Transición a BuildingORB
        if (currentRobotState == RobotState.WaitingMarket && hhmm >= 093000)
        {
            ChangeRobotState(RobotState.BuildingORB, "LOG-00: Inicio de Jornada - Esperando cierre 5m");
        }

        // 3. Cierre Forzoso por Horario
        if (hhmm >= ForceCloseHHMM * 100)
        {
            if (currentRobotState != RobotState.SessionClosed)
            {
                if (entryOrder != null) CancelOrder(entryOrder);
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    LiquidateEverything();
                    ChangeRobotState(RobotState.SessionClosed, "LOG-07: Cierre Forzado ejecutado");
                }
                else
                {
                    ChangeRobotState(RobotState.SessionClosed, "LOG-07: Sesión cerrada (Sin posición)");
                }
            }
            return;
        }

        // 4. Máquina de Estados Principal
        switch (currentRobotState)
        {
            case RobotState.WaitingBreakout:
                if (hhmm >= MaxBreakoutTimeHHMM * 100) 
                { 
                    ChangeRobotState(RobotState.SessionClosed, "Tiempo máximo de breakout alcanzado");
                    PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav"); 
                    return;
                }

                double stopDist = stopDistanciaTicks; // Usamos el precalculado en la serie de 5m
                
                // Ruptura Alcista
                if (Low[0] > (orbUpper + DeltaInput)) 
                { 
                    breakoutDir = 1;
                    stopPrecioFinal = orbMid - stopDist;
                    targetPrecioFinal = orbMid + (stopDist * RatioR);
                    ChangeRobotState(RobotState.SearchingEntry, "LOG-02: Ruptura Alcista confirmada");
                    Draw.ArrowUp(this, "BrkUp" + Time[0].Ticks, true, 0, Low[0] - TickSize * 20, Brushes.Lime);
                    PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav");
                }
                // Ruptura Bajista
                else if (High[0] < (orbLower - DeltaInput)) 
                { 
                    breakoutDir = -1; 
                    stopPrecioFinal = orbMid + stopDist;
                    targetPrecioFinal = orbMid - (stopDist * RatioR);
                    ChangeRobotState(RobotState.SearchingEntry, "LOG-02: Ruptura Bajista confirmada");
                    Draw.ArrowDown(this, "BrkDn" + Time[0].Ticks, true, 0, High[0] + TickSize * 20, Brushes.Red);
                    PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav");
                }
                break;

            case RobotState.SearchingEntry:
                if (!tradeExecutedToday) 
                {
                    ExecuteOrderLogic();
                    string msg = string.Format("LOG-03: Orden LIMIT enviada al Midpoint: {0}", orbMid);
                    ChangeRobotState(RobotState.OrderPending, msg);
                }
                break;

            case RobotState.OrderPending:
                if (hhmm >= TradingEndHHMM * 100) 
                { 
                    LiquidateEverything(); 
                    ChangeRobotState(RobotState.SessionClosed, "LOG-07: Fin ventana trading");
                }
                // El cambio a InTrade ocurre en OnOrderUpdate
                break;

            case RobotState.InTrade:
                if (Position.MarketPosition == MarketPosition.Flat) 
                {
                    ChangeRobotState(RobotState.SessionClosed, "LOG-07: Posición cerrada detectada.");
                }
                break;
        }

        // Renderizado visual (una vez por minuto para no saturar)
        if (orbDefined && currentDay == lastResetDate) RenderLevels();
        UpdateStatusPanel();
    }
}

        private void CreatePanicButton()
        {
            ChartControl.Dispatcher.InvokeAsync(() => {
                chartGrid = (ChartControl.Parent as Grid);
                if (chartGrid == null || panicButton != null) return;
                panicButton = new Button { Content = "PANIC CLOSE", Background = Brushes.Red, Foreground = Brushes.White, FontWeight = FontWeights.Bold, Height = 30, Width = 110, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 45, 0, 0) };
                panicButton.Click += (s, e) => 
					{
						string msg = string.Format("LOG-10: Panic Button Pressed!!!");
		        		Print("[" + Time[0].ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg);	
						LiquidateEverything(); 
						currentRobotState = RobotState.SessionClosed; 		        
					};
                chartGrid.Children.Add(panicButton);
            });
        }

        private void RemovePanicButton()
        {
            if (panicButton != null && chartGrid != null) { ChartControl.Dispatcher.InvokeAsync(() => { chartGrid.Children.Remove(panicButton); panicButton = null; }); }
        }

        private void RenderLevels()
        {
            // Usamos un tag único por día para evitar duplicados en el proceso de "catch-up"
            string tag = lastResetDate.ToString("yyyyMMdd");
            DateTime start = new DateTime(Time[0].Year, Time[0].Month, Time[0].Day, 9, 30, 0);
            DateTime end = new DateTime(Time[0].Year, Time[0].Month, Time[0].Day, TradingEndHHMM / 100, TradingEndHHMM % 100, 0);

            Draw.Line(this, "H" + tag, false, start, orbUpper, end, orbUpper, Brushes.DeepSkyBlue, DashStyleHelper.Solid, 2);
            Draw.Line(this, "L" + tag, false, start, orbLower, end, orbLower, Brushes.DeepSkyBlue, DashStyleHelper.Solid, 2);
            Draw.Line(this, "M" + tag, false, start, orbMid, end, orbMid, Brushes.Gray, DashStyleHelper.Dash, 1);
        }

		private void UpdateStatusPanel()
		{
		    Brush backColor = Brushes.Black;
		    string stCode = "ST-XX";
		
		    // 1. Definición de colores y códigos según el estado actual
		    switch (currentRobotState)
		    {
		        case RobotState.WaitingMarket:
		            stCode = "ST-00 WAITING"; backColor = Brushes.DimGray; break;
		        case RobotState.WaitingBreakout:
		            stCode = "ST-02 WATCHING"; backColor = Brushes.DarkGoldenrod; break;
		        case RobotState.SearchingEntry:
		            stCode = "ST-03 TRAP SET"; backColor = Brushes.DarkCyan; break;
		        case RobotState.InTrade:
		            stCode = "ST-04 IN TRADE"; backColor = Brushes.DarkGreen; break;
		        case RobotState.SessionClosed:
		            stCode = "ST-07 BLOCKED"; backColor = Brushes.DarkRed; break;
		    }
		
		    // 2. Cálculo de Latencia (Monitor de Red)
		    double latency = (DateTime.Now - Time[0]).TotalMilliseconds;
		    string networkStatus = latency > latencia ? "!! STALE DATA !!" : latency.ToString("F0") + "ms";
		    Brush networkColor = latency > latencia ? Brushes.Red : Brushes.LimeGreen;
		
		    // 3. Construcción del String multilínea
			string panelText = string.Format(
			    "{0}\n" +
			    "NET: {1}\n" +
			    "ORB: {2}\n" +
			    "MID: {3}\n" +
			    "RISK: {4} pts | Qty: {5}\n" +
			    "HOY: {6}",
			    stCode,
			    networkStatus,
			    orbDefined ? string.Format("{0:F2} / {1:F2}", orbUpper, orbLower) : "PENDIENTE",
			    orbDefined ? orbMid.ToString("F2") : "---",
			    stopDistanciaTicks > 0 ? stopDistanciaTicks.ToString("F2") : "---",
			    contratosCalculados > 0 ? contratosCalculados.ToString() : "0",
			    (tradeExecutedToday ? "EJECUTADO" : "PENDIENTE")
			);
		
		    // 4. Renderizado en pantalla
		    Draw.TextFixed(this, "StatusPanel", panelText, TextPosition.TopRight, Brushes.White, myFont, 
		                   Brushes.Transparent, backColor, 80); // Opacidad de 80 para ver velas detrás
		}

		private void ExecuteOrderLogic()
		{
		    // Cero cálculos aquí. Solo asignación.
		    SetStopLoss("OrbEntry", CalculationMode.Price, stopPrecioFinal, false);
		    SetProfitTarget("OrbEntry", CalculationMode.Price, targetPrecioFinal);
		
		    if (breakoutDir == 1) 
		        entryOrder = EnterLongLimit(0, true, contratosCalculados, orbMid, "OrbEntry");
		    else 
		        entryOrder = EnterShortLimit(0, true, contratosCalculados, orbMid, "OrbEntry");
		}

		private void CheckBreakEven()
		{
		    // 1. Usamos High/Low para capturar el mechazo dentro del segundo
		    double precioParaGatillo = (breakoutDir == 1) ? High[0] : Low[0];
		    double precioActual = Close[0]; // El precio justo ahora
		    
		    // Trigger usando la distancia calculada en 5m para consistencia total
		    double trigger = (breakoutDir == 1) ? orbMid + (stopDistanciaTicks * BeTriggerRatio) : orbMid - (stopDistanciaTicks * BeTriggerRatio);
		
		    if ((breakoutDir == 1 && precioParaGatillo >= trigger) || (breakoutDir == -1 && precioParaGatillo <= trigger)) 
		    {
		        double precioProteccionBE = (breakoutDir == 1) ? orbMid + BeOffset : orbMid - BeOffset;
		
		        // =========================================================================
		        // LÓGICA DE DECISIÓN DE SALIDA
		        // =========================================================================
		        
		        // Caso A: El precio actual permite mover el SL legalmente (Hay espacio)
		        bool esSeguroModificar = (breakoutDir == 1) 
		            ? (precioActual > precioProteccionBE + (TickSize * 2)) 
		            : (precioActual < precioProteccionBE - (TickSize * 2));
		
		        if (esSeguroModificar)
		        {
		            SetStopLoss("OrbEntry", CalculationMode.Price, precioProteccionBE, false);
		            beTriggered = true;
		            Print(string.Format("[{0}] LOG-05: BE activado. SL movido a {1}", Time[0], precioProteccionBE));
		        }
		        // Caso B: El precio ya regresó y cruzó el nivel de BE (Rebote violento)
		        else 
		        {
		            Print(string.Format("[{0}] LOG-05: BE Gatillado pero precio rebotó a {1}. Liquidando a mercado.", Time[0], precioActual));
		            
		            if (breakoutDir == 1) ExitLong("BE Market Close", "");
		            else ExitShort("BE Market Close", "");
		            
		            beTriggered = true; // Marcamos para que no reintente
		        }
		        
		        // En ambos casos, cancelamos el remanente de la orden de entrada si existe
		        if (entryOrder != null && (entryOrder.OrderState == OrderState.PartFilled || entryOrder.OrderState == OrderState.Working))
		        {
		            CancelOrder(entryOrder);
		        }
		    }
		}
		
		private void LiquidateEverything()
		{
		    // 1. Cancelar la orden de entrada principal si existe y está trabajando
		    if (entryOrder != null)
		    {
		        if (entryOrder.OrderState == OrderState.Working || entryOrder.OrderState == OrderState.Accepted)
		        {
		            CancelOrder(entryOrder);
		        }
		        entryOrder = null; 
		    }
		
		    // 2. Cerrar cualquier posición abierta de esta estrategia
		    if (Position.MarketPosition == MarketPosition.Long) ExitLong("Cierre Forzado", "");
		    else if (Position.MarketPosition == MarketPosition.Short) ExitShort("Cierre Forzado", "");
		    
		    // NOTA: No existe "CancelAllOrders". NinjaTrader gestiona los Stops y Targets 
		    // asociados automáticamente cuando cierras la posición con ExitLong/ExitShort.
		}

		private void ResetStrategy(DateTime day) 
		{ 
		    orbDefined = false; 
		    tradeExecutedToday = false; 
		    beTriggered = false; 
		    lastResetDate = day; 
		    entryOrder = null;
		    breakoutDir = 0;
		    
		    // Limpieza de datos visuales para el panel
		    orbUpper = 0;
		    orbLower = 0;
		    orbMid = 0;
		    stopDistanciaTicks = 0;
		    contratosCalculados = 0;
		}
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (entryOrder != null && entryOrder == order && (orderState == OrderState.Filled || orderState == OrderState.PartFilled)) 
			{ 
				tradeExecutedToday = true; 
				ChangeRobotState(RobotState.InTrade); 
			}
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition) { }
    

		private void ChangeRobotState(RobotState newState, string customMsg = "")
		{
		    if (currentRobotState == newState && string.IsNullOrEmpty(customMsg)) return;
		
		    // Formato: [YYYY-MM-DD HH:mm:ss] [ST-XX] [DESCRIPCIÓN]
		    string timestamp = Time[0].ToString("yyyy-MM-dd HH:mm:ss");
		    string logMsg = string.Format("[{0}] [{1}] {2}", timestamp, newState, customMsg);
		
		    Print(logMsg);
		    Log(logMsg, LogLevel.Information);
		
		    currentRobotState = newState;
		}
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
		    // LOG-04: Ejecución Parcial o Total de Entrada
		    if (entryOrder != null && execution.Order == entryOrder)
		    {
		        if (execution.Order.OrderState == OrderState.Filled || execution.Order.OrderState == OrderState.PartFilled)
		        {
		            string msg = string.Format("LOG-04: Ejecución detectada. Qty Ejecutada: {0} a {1}", execution.Quantity, execution.Price);
		            Print("[" + time.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg);
		        }
		    }
		
		    // LOG-06: Resumen del Trade al Salir (Flat)
		    if (Position.MarketPosition == MarketPosition.Flat && tradeExecutedToday)
		    {
		        // Calculamos PnL en puntos
		        double pnlPoints = 0;
		        if (breakoutDir == 1) pnlPoints = price - orbMid;
		        else pnlPoints = orbMid - price;
		
		        double pnlCash = pnlPoints * quantity * Instrument.MasterInstrument.PointValue;
		
		        // Identificar motivo (SL, TP o Manual)
		        string motivo = execution.Order != null ? execution.Order.Name : "Salida";
		
		        string msg = string.Format("LOG-06: Trade cerrado por {0} en {1}. PnL: {2} pts (${3} USD)", 
		                     motivo, price, pnlPoints.ToString("F2"), pnlCash.ToString("N2"));
		        
		        Print("[" + time.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg);
		        tradeExecutedToday = false; // Reset para evitar logs duplicados
		    }
		}
	}
}
